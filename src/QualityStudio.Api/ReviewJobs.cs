using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Quota;
using ModelPriceCatalog = CodingAgentRunner.Pricing.ModelPriceCatalog;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record StartReviewRequest(
    string Path,
    string Kind,
    string? Model = null,
    string? CliType = null,
    string? ThinkingLevel = null,
    long? TokenCap = null,
    decimal? CostCap = null,
    bool Force = false,
    bool ConfirmBelowFloor = false);

public sealed record ResumeReviewRequest(long? TokenCap = null, decimal? CostCap = null);

public sealed record ReviewPreflightResponse(
    string RepositoryId,
    string Path,
    string Level,
    string Kind,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    ReviewRunEstimate Estimate,
    long? TokenCap,
    decimal? CostCap,
    ReviewModelRecommendation Recommendation,
    bool OverrideBelowFloor,
    string? PreflightResultHash = null,
    int PreflightChecks = 0,
    int PreflightUnavailableChecks = 0);

public sealed record ReviewEstimateDeviation(
    decimal InputTokensPercent,
    decimal OutputTokensPercent,
    decimal? CostPercent,
    string Note);

public sealed record ReviewFileProgress(string Path, string State, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error);

public sealed record ReviewRunResponse(
    string Id,
    string RepositoryId,
    string Path,
    string Level,
    string Kind,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    string State,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ReviewFileProgress> Files,
    IReadOnlyList<string> Errors,
    int UsageOperations,
    TokenUsage Usage,
    ReviewRunEstimate? Estimate,
    long? TokenCap,
    decimal? CostCap,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    int SkippedFiles,
    string? AggregateState,
    string? StopReason,
    ReviewEstimateDeviation? Deviation,
    ReviewModelRecommendation? Recommendation,
    bool RouteOverride,
    string PreflightState = "queued",
    int PreflightChecks = 0,
    int PreflightUnavailableChecks = 0,
    string? PreflightResultHash = null,
    long? PreflightDurationMs = null,
    int BlockedFiles = 0,
    ReviewRunEconomyEvidence? Economy = null);

public interface IReviewExecutor
{
    Task<ReviewExecutionResult> ReviewIfNeededAsync(
        ReviewRequest request,
        bool force,
        CancellationToken cancellationToken);
}

public interface IReviewExecutorFactory
{
    IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
        Action<ReviewUsageEntry> usageRecorded);
}

public sealed class ReviewExecutorFactory(
    SensorRegistry sensors,
    StalenessEvaluator stalenessEvaluator) : IReviewExecutorFactory
{
    public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
        Action<ReviewUsageEntry> usageRecorded) =>
        new ReviewExecutor(new ReviewRunner(new CodingAgentReviewAgent(
                cliType, model, thinkingLevel, eventObserver: eventObserver),
            usageRecorded: usageRecorded, sensorRegistry: sensors, stalenessEvaluator: stalenessEvaluator));

    private sealed class ReviewExecutor(ReviewRunner runner) : IReviewExecutor
    {
        public Task<ReviewExecutionResult> ReviewIfNeededAsync(
            ReviewRequest request,
            bool force,
            CancellationToken cancellationToken) =>
            runner.ReviewIfNeededAsync(request, force, cancellationToken);
    }
}

public sealed class ReviewJobsOptions
{
    public const string SectionName = "ReviewJobs";
    public int MaxConcurrency { get; set; } = 2;
    public int RecentRunLimit { get; set; } = 30;
    public bool UseSnapshotPreflight { get; set; } = true;
}

public sealed class ReviewJobService : BackgroundService
{
    private static readonly HashSet<string> Kinds = ["code", "security", "performance"];
    private readonly Channel<ReviewWorkItem> queue = Channel.CreateUnbounded<ReviewWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, ReviewWorkItem> runs = new(StringComparer.Ordinal);
    private readonly RepositoryRegistry repositories;
    private readonly ReviewJobsOptions options;
    private readonly ILogger<ReviewJobService> logger;
    private readonly QuotaService quotas;
    private readonly RepositoryHierarchyCache hierarchyCache;
    private readonly IReviewExecutorFactory executors;
    private readonly SensorRegistry sensorRegistry;
    private readonly ModelPriceCatalog prices = ModelPriceCatalog.Default;
    private readonly ProjectDashboardService dashboards;
    private readonly ReviewModelCatalog modelCatalog;

    public ReviewJobService(RepositoryRegistry repositories, IOptions<ReviewJobsOptions> options,
        ILogger<ReviewJobService> logger, QuotaService quotas, RepositoryHierarchyCache hierarchyCache,
        IReviewExecutorFactory executors, ProjectDashboardService dashboards, SensorRegistry sensorRegistry,
        ReviewModelCatalog modelCatalog)
    {
        this.repositories = repositories;
        this.options = options.Value;
        this.logger = logger;
        this.quotas = quotas;
        this.hierarchyCache = hierarchyCache;
        this.executors = executors;
        this.dashboards = dashboards;
        this.sensorRegistry = sensorRegistry;
        this.modelCatalog = modelCatalog;
    }

    public async Task<ReviewRunResponse> EnqueueAsync(
        string repositoryId,
        StartReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = PreparePlan(repositoryId, request);
        var (registration, _, node, files) = plan;
        var selection = modelCatalog.Resolve(request.CliType, request.Model, request.ThinkingLevel);
        var cliType = selection.CliType;
        var model = selection.Model;
        var recommendation = modelCatalog.Recommend(request.Kind, node.Level, files.Length);
        var belowFloor = modelCatalog.IsBelowCorrectnessFloor(selection, recommendation);
        if (belowFloor && !request.ConfirmBelowFloor)
            throw new ArgumentException(
                $"The explicit route is below the {recommendation.CorrectnessFloor} correctness floor. Confirm the below-floor override before starting.");
        var (tokenCap, costCap) = ResolveCap(registration, request.TokenCap, request.CostCap);
        var runId = "review-" + Guid.NewGuid().ToString("N");
        var targets = await PrepareTargetsAsync(plan, cancellationToken).ConfigureAwait(false);
        var preflight = options.UseSnapshotPreflight
            ? await CollectPreflightAsync(runId, registration, targets, cancellationToken).ConfigureAwait(false)
            : null;
        var estimate = await EstimateAsync(plan, request.Kind, cliType, model, request.Force, preflight, cancellationToken)
            .ConfigureAwait(false);
        if (costCap.HasValue && estimate.Cost is null)
            throw new ArgumentException($"A cost cap cannot be enforced because model '{model ?? "runner-default"}' has no price in the runner catalogue. Use a token cap instead.");

        var manifest = new ReviewRunManifest(
            runId,
            registration.Id,
            new ReviewRunPlanNode(node.Id, node.Name, node.Path),
            node.Level.ToString().ToLowerInvariant(),
            request.Kind,
            model,
            cliType,
            DateTimeOffset.UtcNow,
            targets,
            AggregateControls(node),
            node.Level == ReviewLevel.File ? null : node.Exclusions,
            estimate,
            tokenCap,
            costCap,
            request.Force,
            selection.ThinkingLevel,
            recommendation,
            selection.Model is not null &&
            (!string.Equals(selection.Model, recommendation.RecommendedModel, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(selection.ThinkingLevel, recommendation.RecommendedThinkingLevel, StringComparison.OrdinalIgnoreCase)));
        var store = new ReviewRunStore(registration.RootPath);
        var item = ReviewWorkItem.Create(manifest, registration, store, preflight);
        store.Create(manifest, item.DurableStatus());
        if (preflight is not null) store.WritePreflight(preflight);
        runs[item.Id] = item;
        if (!queue.Writer.TryWrite(item))
        {
            item.Fail("The review queue is unavailable.");
            throw new InvalidOperationException("The review queue is unavailable.");
        }
        logger.LogInformation(new EventId(1500, "ReviewQueued"),
            "Queued review {ReviewRunId} for {RepositoryId}:{ReviewPath} ({ReviewLevel}, {ReviewKind}, {FileCount} files) via {ReviewCli}/{ReviewModel}/{ReviewThinkingLevel} in {ElapsedMilliseconds} ms",
            item.Id, registration.Id, node.Path, node.Level, item.Kind, targets.Length, item.CliType,
            item.Model ?? "runner-default", item.ThinkingLevel ?? "model-default", stopwatch.ElapsedMilliseconds);
        return item.Snapshot();
    }

    public async Task<ReviewPreflightResponse> EstimateAsync(
        string repositoryId, StartReviewRequest request, CancellationToken cancellationToken = default)
    {
        var plan = PreparePlan(repositoryId, request);
        var selection = modelCatalog.Resolve(request.CliType, request.Model, request.ThinkingLevel);
        var cliType = selection.CliType;
        var model = selection.Model;
        var recommendation = modelCatalog.Recommend(request.Kind, plan.Node.Level, plan.Files.Length);
        var belowFloor = modelCatalog.IsBelowCorrectnessFloor(selection, recommendation);
        var (tokenCap, costCap) = ResolveCap(plan.Registration, request.TokenCap, request.CostCap);
        var targets = await PrepareTargetsAsync(plan, cancellationToken).ConfigureAwait(false);
        var preflight = options.UseSnapshotPreflight
            ? await CollectPreflightAsync("estimate-" + Guid.NewGuid().ToString("N"), plan.Registration, targets, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var estimate = await EstimateAsync(plan, request.Kind, cliType, model, request.Force, preflight, cancellationToken)
            .ConfigureAwait(false);
        if (costCap.HasValue && estimate.Cost is null)
            throw new ArgumentException($"A cost cap cannot be enforced because model '{model ?? "runner-default"}' has no price in the runner catalogue. Use a token cap instead.");
        return new ReviewPreflightResponse(plan.Registration.Id, plan.Node.Path,
            plan.Node.Level.ToString().ToLowerInvariant(), request.Kind, model, selection.ThinkingLevel,
            cliType, estimate, tokenCap, costCap, recommendation, belowFloor,
            preflight?.ResultHash, preflight?.Results.Count ?? 0,
            preflight?.Results.Count(result => result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed) ?? 0);
    }

    private PreparedPlan PreparePlan(string repositoryId, StartReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("A hierarchy path is required.");
        if (!Kinds.Contains(request.Kind)) throw new ArgumentException("Kind must be code, security, or performance.");
        var registration = repositories.Get(repositoryId);
        if (!registration.EnabledReviewKinds.Contains(request.Kind, StringComparer.Ordinal))
            throw new ArgumentException($"Review kind '{request.Kind}' is not enabled for this repository.");

        var access = new RepositoryAccess(registration.RootPath);
        var path = access.NormalizeRelativePath(request.Path);
        var hierarchy = hierarchyCache.Get(registration.RootPath).Roots;
        var node = Flatten(hierarchy).FirstOrDefault(candidate =>
            candidate.Level != ReviewLevel.Function && string.Equals(candidate.Path, path, StringComparison.Ordinal));
        if (node is null) throw new KeyNotFoundException($"No reviewable hierarchy node exists at '{path}'.");
        var files = node.Level == ReviewLevel.File
            ? [node]
            : Flatten([node]).Where(candidate => candidate.Level == ReviewLevel.File)
                .DistinctBy(candidate => candidate.Path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new ArgumentException("The selected node has no reviewable descendant files.");
        return new PreparedPlan(registration, access, node, files);
    }

    private async Task<ReviewRunEstimate> EstimateAsync(
        PreparedPlan plan,
        string kind,
        string cliType,
        string? model,
        bool force,
        PreflightSnapshot? preflight,
        CancellationToken cancellationToken)
    {
        var promptRunner = new ReviewRunner(sensorRegistry: sensorRegistry);
        var measurements = new List<ReviewPromptMeasurement>(plan.Files.Length + 1);
        foreach (var file in plan.Files)
        {
            measurements.Add(await promptRunner.MeasurePromptAsync(
                CreateEstimateRequest(plan, file, ReviewLevel.File, [file.Path], kind, preflight), cancellationToken)
                .ConfigureAwait(false));
        }
        if (plan.Node.Level != ReviewLevel.File)
        {
            measurements.Add(await promptRunner.MeasurePromptAsync(
                CreateEstimateRequest(plan, plan.Node, plan.Node.Level,
                    plan.Files.Select(file => file.Path).ToArray(), kind, preflight), cancellationToken).ConfigureAwait(false));
        }

        var history = await UsageLedger.QueryAsync(plan.Registration.RootPath, kind: kind, recentLimit: 200,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var samples = history.Recent.Where(entry =>
                string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase) &&
                (model is null || string.Equals(entry.Model, model, StringComparison.OrdinalIgnoreCase)) &&
                entry.Tokens.InputTokens is > 0 && entry.Tokens.OutputTokens is >= 0)
            .ToArray();
        if (samples.Length == 0)
        {
            samples = history.Recent.Where(entry => entry.Tokens.InputTokens is > 0 &&
                entry.Tokens.OutputTokens is >= 0).ToArray();
        }
        var outputRatio = samples.Length == 0
            ? 0.20m
            : Math.Clamp(samples.Sum(sample => (decimal)(sample.Tokens.OutputTokens ?? 0)) /
                         samples.Sum(sample => (decimal)(sample.Tokens.InputTokens ?? 0)), 0.01m, 4m);
        var promptCharacters = measurements.Sum(measurement => (long)measurement.Characters);
        var inputTokens = measurements.Sum(measurement => (long)Math.Ceiling(measurement.Characters / 4m));
        var outputTokens = measurements.Sum(measurement =>
            Math.Max(1L, (long)Math.Ceiling(Math.Ceiling(measurement.Characters / 4m) * outputRatio)));
        var cost = prices.ComputeCost(model ?? "runner-default",
            new PricingTokenUsage(inputTokens, outputTokens, 0, 0), DateTime.UtcNow);
        var reviewKind = Enum.Parse<ReviewKind>(kind, ignoreCase: true);
        var expectedFreshSkips = force ? 0 : plan.Files.Count(file =>
            file.AggregatedStates[reviewKind].Direct == ReviewState.Current);
        var beforeCompaction = promptCharacters + PromptCompactionSavings(plan, kind, preflight);
        return new ReviewRunEstimate(plan.Files.Length, measurements.Count, promptCharacters, inputTokens,
            outputTokens, cost.Total, cost.Currency, Camel(cost.Status.ToString()), samples.Length,
            samples.Length == 0
                ? "Input is actual rendered prompt characters / 4; output uses a 20% fallback ratio."
                : $"Input is actual rendered prompt characters / 4; output uses {samples.Length} recorded .quality/usage operation(s).",
            expectedFreshSkips,
            beforeCompaction
        );
    }

    private long PromptCompactionSavings(PreparedPlan plan, string kind, PreflightSnapshot? preflight)
    {
        if (preflight is null) return 0;
        long savings = 0;
        foreach (var subjects in PromptSubjects(plan))
        {
            var projected = PreflightProjection.ForSubjects(preflight.Results, subjects);
            var deterministic = projected.Where(result =>
            {
                try { return sensorRegistry.Get(result.Check.Id) is IDeterministicEvidenceSensor; }
                catch (SensorNotFoundException) { return false; }
            }).ToArray();
            savings += PreflightProjection.ToPromptJson(deterministic, int.MaxValue).Length -
                       PreflightProjection.ToPromptJson(deterministic).Length;
            if (kind != "security") continue;
            var security = SecurityEvidenceCollector.FromPreflight(
                projected, subjects, SecurityConfigurations(plan.Registration));
            savings += security.ToPromptJson(int.MaxValue).Length - security.ToPromptJson().Length;
        }
        return Math.Max(0, savings);
    }

    private static IEnumerable<IReadOnlyList<string>> PromptSubjects(PreparedPlan plan)
    {
        foreach (var file in plan.Files) yield return new[] { file.Path };
        if (plan.Node.Level != ReviewLevel.File)
            yield return plan.Files.Select(file => file.Path).ToArray();
    }

    private ReviewRequest CreateEstimateRequest(
        PreparedPlan plan,
        HierarchyNode node,
        ReviewLevel level,
        IReadOnlyList<string> files,
        string kind,
        PreflightSnapshot? preflight) =>
        new(node.Path, kind, level,
            RepositoryRoot: plan.Registration.RootPath,
            GlobalInputsDirectory: plan.Registration.GlobalInputsDirectory,
            InputBudgetCharacters: plan.Registration.InputBudgetCharacters,
            UnitId: node.Id,
            SubjectFiles: files,
            DisplayName: node.Name,
            SubjectUnits: level == ReviewLevel.File
                ? null
                : plan.Files.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
            AggregateControls: AggregateControls(plan.Node),
            AggregateExclusions: level == ReviewLevel.File ? null : plan.Node.Exclusions,
            Sensors: kind == "security" ? SecurityConfigurations(plan.Registration) : null,
            PreflightEvidence: preflight?.Results);

    private static async Task<ReviewRunPlanTarget[]> PrepareTargetsAsync(
        PreparedPlan plan,
        CancellationToken cancellationToken)
    {
        var targets = new List<ReviewRunPlanTarget>(plan.Files.Length);
        foreach (var file in plan.Files)
        {
            var subjectHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(
                plan.Access.ResolveFile(file.Path), cancellationToken).ConfigureAwait(false);
            targets.Add(new ReviewRunPlanTarget(file.Id, file.Name, file.Path, subjectHash));
        }
        return targets.ToArray();
    }

    private async Task<PreflightSnapshot> CollectPreflightAsync(
        string runId,
        RepositoryRegistration registration,
        IReadOnlyList<ReviewRunPlanTarget> targets,
        CancellationToken cancellationToken)
    {
        var subject = await CreatePreflightSubjectAsync(
            registration.RootPath, targets, cancellationToken).ConfigureAwait(false);
        return await new PreflightCollector(sensorRegistry).CollectAsync(
            runId,
            registration.RootPath,
            subject,
            EnabledConfigurations(registration),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PreflightSubject> CreatePreflightSubjectAsync(
        string root,
        IReadOnlyList<ReviewRunPlanTarget> targets,
        CancellationToken cancellationToken)
    {
        var hashes = targets.ToDictionary(target => target.Path, target => target.SubjectHash, StringComparer.Ordinal);
        foreach (var path in EnumeratePreflightInputs(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (hashes.ContainsKey(relative)) continue;
            hashes[relative] = await ReviewSubjectHasher.ComputeFileContentHashAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        return PreflightSubject.Create(hashes);
    }

    private static IReadOnlyList<string> EnumeratePreflightInputs(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsPreflightInput(root, path)));
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                if (Path.GetFileName(child) is not (".git" or ".quality" or "bin" or "obj" or "node_modules" or "dist" or "out-tsc"))
                    pending.Push(child);
            }
        }

        foreach (var area in new[] { "security", "static-analysis" })
        {
            var directory = Path.Combine(root, ".quality", area);
            if (!Directory.Exists(directory)) continue;
            files.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => IsPreflightInput(root, path)));
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsPreflightInput(string root, string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.StartsWith(".quality/security/", StringComparison.Ordinal) ||
            relative.Equals(".quality/static-analysis/style-baseline.json", StringComparison.Ordinal))
            return true;
        return name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("CodeMetricsConfig.txt", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("angular.json", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) && extension == ".json" ||
               name.StartsWith("eslint.config.", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".eslintrc", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".prettier", StringComparison.OrdinalIgnoreCase) ||
               extension is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx";
    }

    private static IReadOnlyList<ReviewSensorConfiguration> EnabledConfigurations(
        RepositoryRegistration registration) => (registration.Sensors ?? [])
        .Where(sensor => sensor.Enabled)
        .Select(sensor => new ReviewSensorConfiguration(
            sensor.Id,
            sensor.Configuration,
            sensor.Required,
            sensor.CommandId))
        .ToArray();

    private IReadOnlyList<ReviewSensorConfiguration> SecurityConfigurations(
        RepositoryRegistration registration) => EnabledConfigurations(registration)
        .Where(configuration => sensorRegistry.Get(configuration.Id) is ISecurityEvidenceSensor)
        .ToArray();

    private static (long? TokenCap, decimal? CostCap) ResolveCap(
        RepositoryRegistration registration, long? requestedTokens, decimal? requestedCost)
    {
        if (requestedTokens.HasValue && requestedCost.HasValue)
            throw new ArgumentException("Choose either a token cap or a cost cap, not both.");
        var tokenCap = requestedTokens ?? (requestedCost.HasValue ? null : registration.DefaultReviewTokenCap);
        var costCap = requestedCost ?? (requestedTokens.HasValue ? null : registration.DefaultReviewCostCap);
        if (tokenCap is <= 0 or > 1_000_000_000) throw new ArgumentException("Token cap must be between 1 and 1,000,000,000.");
        if (costCap is <= 0 or > 1_000_000) throw new ArgumentException("Cost cap must be between 0 and 1,000,000.");
        return (tokenCap, costCap);
    }

    private static string Camel(string value) => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed record PreparedPlan(
        RepositoryRegistration Registration,
        RepositoryAccess Access,
        HierarchyNode Node,
        HierarchyNode[] Files);

    public IReadOnlyList<ReviewRunResponse> List(string repositoryId) => runs.Values
        .Where(run => string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(run => run.CreatedAt).Take(Math.Max(1, options.RecentRunLimit)).Select(run => run.Snapshot()).ToArray();

    public ReviewRunResponse Get(string repositoryId, string id) => Find(repositoryId, id).Snapshot();

    public PreflightSnapshot GetPreflight(string repositoryId, string id) =>
        Find(repositoryId, id).Preflight
        ?? throw new KeyNotFoundException($"Review run '{id}' has no preflight snapshot.");

    public ReviewRunResponse Cancel(string repositoryId, string id)
    {
        var run = Find(repositoryId, id);
        run.Cancel();
        logger.LogInformation(new EventId(1503, "ReviewCancellationRequested"),
            "Cancellation requested for review {ReviewRunId}", id);
        return run.Snapshot();
    }

    public ReviewRunResponse Pause(string repositoryId, string id)
    {
        var run = Find(repositoryId, id);
        run.Pause();
        logger.LogInformation(new EventId(1508, "ReviewPauseRequested"),
            "Pause requested for review {ReviewRunId}", id);
        return run.Snapshot();
    }

    public ReviewRunResponse Resume(string repositoryId, string id, ResumeReviewRequest? request = null)
    {
        var run = Find(repositoryId, id);
        if (run.Resume(request?.TokenCap, request?.CostCap) && !queue.Writer.TryWrite(run))
        {
            run.Fail("The review queue is unavailable.");
            throw new InvalidOperationException("The review queue is unavailable.");
        }
        logger.LogInformation(new EventId(1509, "ReviewResumeRequested"),
            "Resume requested for review {ReviewRunId}", id);
        return run.Snapshot();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RecoverRuns();
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (item.State != "queued") continue;
                await RunAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(new EventId(1506, "ReviewQueueStopped"), "Review queue stopped with the API host");
        }
    }

    private void RecoverRuns()
    {
        var recovered = 0;
        foreach (var registration in repositories.List())
        {
            var store = new ReviewRunStore(registration.RootPath);
            foreach (var stored in store.LoadAll((directory, exception) =>
                         logger.LogError(new EventId(1511, "ReviewRunRecoveryFailed"), exception,
                             "Could not load durable review run from {ReviewRunDirectory}", directory)))
            {
                if (!string.Equals(stored.Manifest.RepositoryId, registration.Id, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(new EventId(1512, "ReviewRunRepositoryMismatch"),
                        "Skipped review {ReviewRunId} because manifest repository {ManifestRepositoryId} does not match {RepositoryId}",
                        stored.Manifest.RunId, stored.Manifest.RepositoryId, registration.Id);
                    continue;
                }
                try
                {
                    var item = ReviewWorkItem.Restore(stored, registration, store);
                    if (!runs.TryAdd(item.Id, item)) continue;
                    if (!ReviewRunStore.IsTerminal(item.State))
                    {
                        item.PrepareForRecovery();
                        if (item.State == "queued") queue.Writer.TryWrite(item);
                        recovered++;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    logger.LogError(new EventId(1511, "ReviewRunRecoveryFailed"), exception,
                        "Could not restore durable review {ReviewRunId}", stored.Manifest.RunId);
                }
            }
        }
        if (recovered > 0)
        {
            logger.LogInformation(new EventId(1507, "ReviewRunsRecovered"),
                "Recovered {ReviewRunCount} non-terminal review runs", recovered);
        }
    }

    private async Task RunAsync(ReviewWorkItem item, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attemptToken = item.Start();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, attemptToken);
        logger.LogInformation(new EventId(1501, "ReviewStarted"),
            "Started review {ReviewRunId} via {ReviewCli}/{ReviewModel}/{ReviewThinkingLevel}", item.Id,
            item.CliType, item.Model ?? "runner-default", item.ThinkingLevel ?? "model-default");
        try
        {
            if (options.UseSnapshotPreflight)
            {
                var preflight = await EnsurePreflightCurrentAsync(item, linked.Token).ConfigureAwait(false);
                var unavailableReason = RequiredAvailabilityBlockingReason(preflight, item.Kind);
                if (unavailableReason is not null)
                {
                    item.BlockPreflight(unavailableReason);
                    return;
                }
            }
            else
            {
                item.DeterministicEvidence = await new DeterministicEvidenceCollector(sensorRegistry)
                    .CollectAsync(
                        item.Repository.RootPath,
                        EnabledConfigurations(item.Repository),
                        linked.Token)
                    .ConfigureAwait(false);
            }
            if (item.HasCap)
            {
                foreach (var file in item.PendingFiles())
                {
                    if (item.TryStopAtCap()) break;
                    await RunFileAsync(item, file, linked.Token).ConfigureAwait(false);
                    if (item.TryStopAtCap()) break;
                }
            }
            else
            {
                await Parallel.ForEachAsync(item.PendingFiles(),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrency, 1, 16),
                        CancellationToken = linked.Token,
                    },
                    (file, cancellationToken) => RunFileAsync(item, file, cancellationToken)).ConfigureAwait(false);
            }

            if (item.State == "running" && item.Node.Level != ReviewLevel.File)
            {
                if (!item.TryStopAtCap())
                {
                    if (options.UseSnapshotPreflight)
                    {
                        var preflight = await EnsurePreflightCurrentAsync(item, linked.Token).ConfigureAwait(false);
                        var reason = OperationBlockingReason(
                            preflight,
                            item.Kind,
                            item.Node.Level,
                            item.Files.Select(file => file.Path).ToArray());
                        if (reason is not null)
                        {
                            item.BlockAggregatePreflight(reason);
                        }
                    }
                    if (item.StartAggregate())
                    {
                        var execution = await CreateRunner(item).ReviewIfNeededAsync(
                            CreateRequest(item, item.Node, item.Node.Level, item.Files.Select(file => file.Path).ToArray()),
                            item.Force,
                            linked.Token).ConfigureAwait(false);
                        if (execution.SkippedFresh) item.SkipAggregateFresh(); else item.FinishAggregate();
                    }
                }
            }
            if (item.Complete())
            {
                logger.LogInformation(new EventId(1502, "ReviewCompleted"),
                    "Completed review {ReviewRunId} with {FailedFileCount} failed files in {ElapsedMilliseconds} ms",
                    item.Id, item.FailedFiles, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            item.StopAttempt();
        }
        catch (Exception exception)
        {
            item.Fail(exception.Message);
            logger.LogError(new EventId(1505, "ReviewFailed"), exception, "Review {ReviewRunId} failed", item.Id);
        }
        finally
        {
            if (item.EndAttempt() && !queue.Writer.TryWrite(item))
            {
                item.Fail("The review queue is unavailable.");
            }
        }
    }

    private async ValueTask RunFileAsync(
        ReviewWorkItem item, HierarchyNode file, CancellationToken cancellationToken)
    {
        if (options.UseSnapshotPreflight)
        {
            var preflight = await EnsurePreflightCurrentAsync(item, cancellationToken).ConfigureAwait(false);
            var unavailableReason = RequiredAvailabilityBlockingReason(preflight, item.Kind);
            if (unavailableReason is not null)
            {
                item.BlockPreflight(unavailableReason);
                return;
            }
            var reason = OperationBlockingReason(preflight, item.Kind, ReviewLevel.File, [file.Path]);
            if (reason is not null)
            {
                item.BlockFilePreflight(file.Path, reason);
                return;
            }
        }
        if (!item.StartFile(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        try
        {
            var execution = await CreateRunner(item).ReviewIfNeededAsync(
                CreateRequest(item, file, ReviewLevel.File, [file.Path]),
                item.Force,
                cancellationToken).ConfigureAwait(false);
            if (execution.SkippedFresh) item.SkipFileFresh(file.Path); else item.FinishFile(file.Path, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (item.State == "cancelled") item.CancelFile(file.Path); else item.RequeueFile(file.Path);
            throw;
        }
        catch (Exception exception)
        {
            item.FinishFile(file.Path, exception.Message);
            logger.LogError(new EventId(1504, "ReviewFileFailed"), exception,
                "File {ReviewFilePath} failed in review {ReviewRunId}", file.Path, item.Id);
        }
    }

    private async Task<PreflightSnapshot> EnsurePreflightCurrentAsync(
        ReviewWorkItem item,
        CancellationToken cancellationToken)
    {
        await item.PreflightLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var access = new RepositoryAccess(item.Repository.RootPath);
            var hashes = new List<KeyValuePair<string, string>>(item.Files.Count);
            foreach (var file in item.Files)
            {
                var hash = await ReviewSubjectHasher.ComputeFileContentHashAsync(
                    access.ResolveFile(file.Path), cancellationToken).ConfigureAwait(false);
                hashes.Add(KeyValuePair.Create(file.Path, hash));
            }
            var targets = hashes.Select(hash => new ReviewRunPlanTarget(
                hash.Key, Path.GetFileName(hash.Key), hash.Key, hash.Value)).ToArray();
            var subject = await CreatePreflightSubjectAsync(
                item.Repository.RootPath, targets, cancellationToken).ConfigureAwait(false);
            var configurations = EnabledConfigurations(item.Repository);
            var configurationHash = new PreflightCollector(sensorRegistry).ConfigurationSetHash(configurations);
            var existing = item.Preflight;
            if (existing is not null &&
                string.Equals(existing.Subject.ManifestHash, subject.ManifestHash, StringComparison.Ordinal) &&
                string.Equals(existing.ConfigurationHash, configurationHash, StringComparison.Ordinal))
            {
                item.RecordPreflightCacheHit();
                return existing;
            }

            item.StartPreflight();
            try
            {
                var snapshot = await new PreflightCollector(sensorRegistry).CollectAsync(
                    item.Id,
                    item.Repository.RootPath,
                    subject,
                    configurations,
                    cancellationToken).ConfigureAwait(false);
                item.FinishPreflight(snapshot);
                return snapshot;
            }
            catch
            {
                item.FailPreflight();
                throw;
            }
        }
        finally
        {
            item.PreflightLock.Release();
        }
    }

    private string? RequiredAvailabilityBlockingReason(PreflightSnapshot preflight, string kind)
    {
        var checks = preflight.Results
            .Where(result => result.Check.Required &&
                IsRelevantToKind(result.Check.Id, kind) &&
                result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed)
            .Select(result => result.Check.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return checks.Length == 0
            ? null
            : $"Required preflight check(s) unavailable: {string.Join(", ", checks)}.";
    }

    private string? OperationBlockingReason(
        PreflightSnapshot preflight,
        string kind,
        ReviewLevel level,
        IReadOnlyList<string> subjectPaths)
    {
        var subjects = subjectPaths.Select(NormalizeSensorPath)
            .ToHashSet(StringComparer.Ordinal);
        var checks = preflight.Results.Where(result =>
                result.Status == PreflightStatus.Blocked &&
                IsRelevantToKind(result.Check.Id, kind) &&
                result.Check.GateDisposition switch
                {
                    PreflightGateDisposition.BlockAffectedSubjects => result.Findings.Any(finding =>
                        finding.Locations.Count == 0 || finding.Locations.Any(location =>
                            subjects.Contains(NormalizeSensorPath(location.Path)))),
                    PreflightGateDisposition.BlockProjectPerformance =>
                        level != ReviewLevel.File && string.Equals(kind, "performance", StringComparison.Ordinal),
                    _ => false,
                })
            .Select(result => result.Check.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return checks.Length == 0
            ? null
            : $"Deterministic preflight finding(s) blocked this operation: {string.Join(", ", checks)}.";
    }

    private bool IsRelevantToKind(string sensorId, string kind)
    {
        var sensor = sensorRegistry.Get(sensorId);
        return sensor is not ISecurityEvidenceSensor || string.Equals(kind, "security", StringComparison.Ordinal);
    }

    private static string NormalizeSensorPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    private ReviewWorkItem Find(string repositoryId, string id)
    {
        if (!runs.TryGetValue(id, out var run) ||
            !string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Review run '{id}' was not found.");
        return run;
    }

    private IReviewExecutor CreateRunner(ReviewWorkItem item) => executors.Create(
        item.CliType, item.Model, item.ThinkingLevel,
        (_, runEvent) => quotas.Observe(item.CliType, runEvent), item.AddUsage);

    private ReviewRequest CreateRequest(
        ReviewWorkItem item,
        HierarchyNode node,
        ReviewLevel level,
        IReadOnlyList<string> files)
    {
        var architectureContext = level == ReviewLevel.Project &&
                                  string.Equals(item.Kind, "code", StringComparison.Ordinal)
            ? dashboards.ArchitectureReviewContext(
                item.Repository.RootPath,
                hierarchyCache.Get(item.Repository.RootPath))
            : null;
        return new ReviewRequest(node.Path, item.Kind, level,
            ProjectGuidelines: architectureContext,
            RepositoryRoot: item.Repository.RootPath,
            GlobalInputsDirectory: item.Repository.GlobalInputsDirectory,
            InputBudgetCharacters: item.Repository.InputBudgetCharacters,
            UnitId: node.Id,
            SubjectFiles: files,
            DisplayName: node.Name,
            SubjectUnits: level == ReviewLevel.File
                ? null
                : item.Files.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
            AggregateControls: item.AggregateControls,
            AggregateExclusions: item.AggregateExclusions,
            ReviewRunId: item.Id,
            Sensors: item.Kind == "security"
                ? SecurityConfigurations(item.Repository)
                : null,
            DeterministicEvidence: options.UseSnapshotPreflight ? null : item.DeterministicEvidence,
            PreflightEvidence: options.UseSnapshotPreflight ? item.Preflight?.Results : null);
    }

    private static IReadOnlyList<string>? AggregateControls(HierarchyNode node) => node.Level switch
    {
        ReviewLevel.Project => (node.Path == "." ? [] : new[] { node.Path })
            .Concat(Flatten([node]).Where(candidate => candidate.Level == ReviewLevel.Module).Select(candidate => candidate.Path))
            .Distinct(StringComparer.Ordinal).ToArray(),
        ReviewLevel.Module => [node.Path],
        _ => null,
    };

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }

    private sealed class ReviewWorkItem
    {
        private readonly object gate = new();
        private readonly ReviewRunManifest manifest;
        private readonly ReviewRunStore store;
        private readonly Dictionary<string, MutableFileProgress> progress;
        private readonly List<string> errors;
        private readonly SemaphoreSlim preflightLock = new(1, 1);
        private TokenUsage usage;
        private CancellationTokenSource attemptCancellation = new();
        private int usageOperations;
        private long? tokenCap;
        private decimal? costCap;
        private decimal? costSpent;
        private string? currency;
        private string priceStatus;
        private string? aggregateState;
        private string? stopReason;
        private bool attemptActive;
        private bool resumePending;
        private string state;
        private string preflightState;
        private long? preflightDurationMs;
        private int preflightCacheHits;
        private PreflightSnapshot? preflight;

        private ReviewWorkItem(
            ReviewRunManifest manifest,
            RepositoryRegistration repository,
            ReviewRunStore store,
            ReviewRunStatus? status,
            IReadOnlyList<ReviewRunFileTransition>? transitions,
            PreflightSnapshot? preflight)
        {
            this.manifest = manifest;
            this.store = store;
            Repository = repository;
            if (!Enum.TryParse<ReviewLevel>(manifest.Level, ignoreCase: true, out var level) || level == ReviewLevel.Function)
                throw new ArgumentException($"Review manifest has unsupported level '{manifest.Level}'.");
            Node = new HierarchyNode(manifest.Node.Id, manifest.Node.Name, level, manifest.Node.Path);
            Files = manifest.Targets.Select(target =>
                new HierarchyNode(target.Id, target.Name, ReviewLevel.File, target.Path)).ToArray();
            progress = manifest.Targets.ToDictionary(
                target => target.Path,
                target => new MutableFileProgress(target.Path),
                StringComparer.Ordinal);
            state = status?.State ?? "queued";
            StartedAt = status?.StartedAt;
            FinishedAt = status?.FinishedAt;
            errors = status?.Errors.ToList() ?? [];
            usageOperations = status?.UsageOperations ?? 0;
            usage = status?.Usage ?? new TokenUsage(null, null, null, null, 0);
            tokenCap = status?.TokenCap ?? manifest.TokenCap;
            costCap = status?.CostCap ?? manifest.CostCap;
            costSpent = status?.CostSpent ?? (status is null && manifest.Estimate?.Cost is not null ? 0m : null);
            currency = status?.Currency ?? manifest.Estimate?.Currency;
            priceStatus = status?.PriceStatus ?? manifest.Estimate?.PriceStatus ?? "unknownModel";
            aggregateState = status?.AggregateState ?? (Node.Level == ReviewLevel.File ? null : "queued");
            stopReason = status?.StopReason;
            Preflight = preflight;
            preflightState = status?.PreflightState ?? (preflight is null
                ? "queued"
                : preflight.Results.Any(result => result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed)
                    ? "unavailable"
                    : "done");
            preflightDurationMs = status?.PreflightDurationMs ?? preflight?.DurationMs;
            preflightCacheHits = status?.PreflightCacheHits ?? 0;
            if (transitions is not null)
            {
                foreach (var transition in transitions)
                {
                    if (!progress.TryGetValue(transition.Path, out var file) ||
                        transition.State is not ("queued" or "running" or "done" or "failed" or "cancelled" or "skipped" or "skipped-fresh" or "blocked-preflight")) continue;
                    file.State = transition.State;
                    file.StartedAt = transition.StartedAt;
                    file.FinishedAt = transition.FinishedAt;
                    file.Error = transition.Error;
                }
                foreach (var file in progress.Values.Where(file => file.State == "failed" && file.Error is not null))
                {
                    var error = $"{file.Path}: {file.Error}";
                    if (!errors.Contains(error, StringComparer.Ordinal)) errors.Add(error);
                }
            }
        }

        public static ReviewWorkItem Create(
            ReviewRunManifest manifest,
            RepositoryRegistration repository,
            ReviewRunStore store,
            PreflightSnapshot? preflight = null) => new(manifest, repository, store, null, null, preflight);

        public static ReviewWorkItem Restore(
            StoredReviewRun stored,
            RepositoryRegistration repository,
            ReviewRunStore store) => new(stored.Manifest, repository, store, stored.Status, stored.Progress,
                ReviewRunStore.IsTerminal(stored.Status.State) ? stored.Preflight : null);

        public string Id => manifest.RunId;
        public RepositoryRegistration Repository { get; }
        public HierarchyNode Node { get; }
        public IReadOnlyList<HierarchyNode> Files { get; }
        public IReadOnlyList<string>? AggregateControls => manifest.AggregateControls;
        public IReadOnlyList<ScopeExclusion>? AggregateExclusions => manifest.AggregateExclusions;
        public string Kind => manifest.Kind;
        public string? Model => manifest.Model;
        public string? ThinkingLevel => manifest.ThinkingLevel;
        public string CliType => manifest.CliType;
        public bool Force => manifest.Force;
        public DateTimeOffset CreatedAt => manifest.CreatedAt;
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public string State { get { lock (gate) return state; } }
        public int FailedFiles { get { lock (gate) return progress.Values.Count(file => file.State == "failed"); } }
        public bool HasCap { get { lock (gate) return tokenCap.HasValue || costCap.HasValue; } }
        public IReadOnlyList<SensorScanResult> DeterministicEvidence { get; set; } = [];
        public SemaphoreSlim PreflightLock => preflightLock;
        public PreflightSnapshot? Preflight { get { lock (gate) return preflight; } private set => preflight = value; }

        public void PrepareForRecovery()
        {
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                foreach (var file in progress.Values.Where(file => file.State == "running")) RequeueFileCore(file);
                if (aggregateState == "running") aggregateState = "queued";
                state = state == "paused" ? "paused" : "queued";
                if (Preflight is null) preflightState = "queued";
                FinishedAt = null;
                PersistStatus();
            }
        }

        public void StartPreflight()
        {
            lock (gate)
            {
                preflightState = "running";
                PersistStatus();
            }
        }

        public void FinishPreflight(PreflightSnapshot snapshot)
        {
            lock (gate)
            {
                Preflight = snapshot;
                preflightState = snapshot.Results.Any(result =>
                    result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed)
                    ? "unavailable"
                    : "done";
                preflightDurationMs = snapshot.DurationMs;
                store.WritePreflight(snapshot);
                PersistStatus();
            }
        }

        public void RecordPreflightCacheHit()
        {
            lock (gate)
            {
                preflightCacheHits++;
                PersistStatus();
            }
        }

        public void FailPreflight()
        {
            lock (gate)
            {
                preflightState = "failed";
                PersistStatus();
            }
        }

        public void BlockPreflight(string reason)
        {
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                state = "blocked-preflight";
                preflightState = "blocked";
                stopReason = reason;
                FinishedAt = DateTimeOffset.UtcNow;
                foreach (var file in progress.Values.Where(file => file.State is "queued" or "running"))
                {
                    file.State = "blocked-preflight";
                    file.FinishedAt = FinishedAt;
                    file.Error = reason;
                    AppendProgress(file);
                }
                if (aggregateState is "queued" or "running") aggregateState = "blocked-preflight";
                PersistStatus();
            }
        }

        public void BlockFilePreflight(string path, string reason)
        {
            lock (gate)
            {
                if (state != "running") return;
                var file = progress[path];
                if (file.State != "queued") return;
                file.State = "blocked-preflight";
                file.FinishedAt = DateTimeOffset.UtcNow;
                file.Error = reason;
                Append(file);
            }
        }

        public void BlockAggregatePreflight(string reason)
        {
            lock (gate)
            {
                if (state != "running" || aggregateState != "queued") return;
                aggregateState = "blocked-preflight";
                stopReason = reason;
                PersistStatus();
            }
        }

        public CancellationToken Start()
        {
            lock (gate)
            {
                if (state != "queued") throw new InvalidOperationException($"Review '{Id}' is not queued.");
                state = "running";
                StartedAt ??= DateTimeOffset.UtcNow;
                FinishedAt = null;
                attemptActive = true;
                resumePending = false;
                PersistStatus();
                return attemptCancellation.Token;
            }
        }

        public IReadOnlyList<HierarchyNode> PendingFiles()
        {
            lock (gate)
            {
                return Files.Where(file => progress[file.Path].State == "queued").ToArray();
            }
        }

        public bool StartFile(string path)
        {
            lock (gate)
            {
                var file = progress[path];
                if (state != "running" || file.State != "queued") return false;
                file.State = "running";
                file.StartedAt = DateTimeOffset.UtcNow;
                file.FinishedAt = null;
                file.Error = null;
                Append(file);
                return true;
            }
        }

        public void FinishFile(string path, string? error)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State != "running") return;
                file.State = error is null ? "done" : "failed";
                file.Error = error;
                file.FinishedAt = DateTimeOffset.UtcNow;
                if (error is not null) errors.Add($"{path}: {error}");
                Append(file);
            }
        }

        public void SkipFileFresh(string path)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State != "running") return;
                file.State = "skipped-fresh";
                file.Error = null;
                file.FinishedAt = DateTimeOffset.UtcNow;
                Append(file);
            }
        }

        public void RequeueFile(string path)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State == "running") RequeueFileCore(file);
            }
        }

        public void CancelFile(string path)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State == "cancelled") return;
                file.State = "cancelled";
                file.FinishedAt = DateTimeOffset.UtcNow;
                Append(file);
            }
        }

        public bool StartAggregate()
        {
            lock (gate)
            {
                if (state != "running" || aggregateState != "queued") return false;
                aggregateState = "running";
                PersistStatus();
                return true;
            }
        }

        public void FinishAggregate()
        {
            lock (gate)
            {
                if (aggregateState != "running") return;
                aggregateState = "done";
                PersistStatus();
            }
        }

        public void SkipAggregateFresh()
        {
            lock (gate)
            {
                if (aggregateState != "running") return;
                aggregateState = "skipped-fresh";
                PersistStatus();
            }
        }

        public void AddUsage(ReviewUsageEntry entry)
        {
            lock (gate)
            {
                var operationUsage = entry.Tokens;
                usageOperations++;
                usage = new TokenUsage(
                    Add(usage.InputTokens, operationUsage.InputTokens),
                    Add(usage.OutputTokens, operationUsage.OutputTokens),
                    Add(usage.CachedInputTokens, operationUsage.CachedInputTokens),
                    Add(usage.ReasoningOutputTokens, operationUsage.ReasoningOutputTokens),
                    usage.DurationMs + operationUsage.DurationMs);
                var input = Math.Max(0, operationUsage.InputTokens ?? 0);
                var cached = Math.Clamp(operationUsage.CachedInputTokens ?? 0, 0, input);
                var operationCost = ModelPriceCatalog.Default.ComputeCost(entry.Model,
                    new PricingTokenUsage(input - cached, Math.Max(0, operationUsage.OutputTokens ?? 0), cached, 0),
                    entry.Timestamp.UtcDateTime);
                priceStatus = Camel(operationCost.Status.ToString());
                currency = operationCost.Currency ?? currency;
                costSpent = operationCost.Total.HasValue && (costSpent.HasValue || usageOperations == 1)
                    ? (costSpent ?? 0m) + operationCost.Total.Value
                    : null;
                PersistStatus();
            }
        }

        public bool TryStopAtCap()
        {
            lock (gate)
            {
                if (state != "running" || !CapReached()) return state == "capped";
                var hasRemainingFiles = progress.Values.Any(file => file.State == "queued");
                var hasAggregate = aggregateState == "queued";
                if (!hasRemainingFiles && !hasAggregate) return false;
                state = "capped";
                FinishedAt = DateTimeOffset.UtcNow;
                stopReason = costCap.HasValue && costSpent is null
                    ? $"Cost cap enforcement stopped because actual model pricing is unavailable ({priceStatus}). Resume with a token cap."
                    : tokenCap.HasValue
                    ? $"Token cap of {tokenCap.Value:N0} reached after {ConsumedTokens():N0} tokens."
                    : $"Cost cap of {costCap!.Value:0.####} {currency ?? "USD"} reached after {costSpent:0.####} {currency ?? "USD"}.";
                foreach (var file in progress.Values.Where(file => file.State == "queued"))
                {
                    file.State = "skipped";
                    file.FinishedAt = FinishedAt;
                    file.Error = stopReason;
                    AppendProgress(file);
                }
                if (aggregateState == "queued") aggregateState = "skipped";
                PersistStatus();
                return true;
            }
        }

        public bool Complete()
        {
            lock (gate)
            {
                if (state != "running") return false;
                state = "done";
                if (aggregateState == "running") aggregateState = "done";
                FinishedAt = DateTimeOffset.UtcNow;
                PersistStatus();
                return true;
            }
        }

        public void Fail(string error)
        {
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                state = "failed";
                if (aggregateState == "running") aggregateState = "failed";
                errors.Add(error);
                FinishedAt = DateTimeOffset.UtcNow;
                PersistStatus();
            }
        }

        public void Cancel()
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                state = "cancelled";
                FinishedAt = DateTimeOffset.UtcNow;
                // Make the terminal intent durable before updating individual files. If the
                // process stops during the loop, startup must still never resume this run.
                PersistStatus();
                foreach (var file in progress.Values.Where(file => file.State is "queued" or "running"))
                {
                    file.State = "cancelled";
                    file.FinishedAt = FinishedAt;
                    AppendProgress(file);
                }
                if (aggregateState is "queued" or "running") aggregateState = "cancelled";
                PersistStatus();
                cancellation = attemptCancellation;
            }
            cancellation.Cancel();
        }

        public void Pause()
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                if (state == "paused") return;
                if (ReviewRunStore.IsTerminal(state))
                    throw new ArgumentException($"Terminal review '{Id}' cannot be paused.");
                state = "paused";
                FinishedAt = null;
                PersistStatus();
                cancellation = attemptCancellation;
            }
            cancellation.Cancel();
        }

        public bool Resume(long? newTokenCap, decimal? newCostCap)
        {
            lock (gate)
            {
                if (state is not ("paused" or "capped"))
                    throw new ArgumentException($"Review '{Id}' is not paused or capped.");
                if (state == "capped")
                {
                    if (newTokenCap.HasValue && newCostCap.HasValue)
                        throw new ArgumentException("Choose either a token cap or a cost cap, not both.");
                    if (!newTokenCap.HasValue && !newCostCap.HasValue)
                        throw new ArgumentException("Resuming a capped review requires a higher token or cost cap.");
                    if (newTokenCap is <= 0 or > 1_000_000_000 || newCostCap is <= 0 or > 1_000_000)
                        throw new ArgumentException("The replacement cap is outside the supported range.");
                    tokenCap = newTokenCap;
                    costCap = newCostCap;
                    if (CapReached()) throw new ArgumentException("The replacement cap must be higher than the run's current spend.");
                    foreach (var file in progress.Values.Where(file => file.State == "skipped")) RequeueFileCore(file);
                    if (aggregateState == "skipped") aggregateState = "queued";
                    stopReason = null;
                }
                attemptCancellation.Dispose();
                attemptCancellation = new CancellationTokenSource();
                state = "queued";
                FinishedAt = null;
                if (attemptActive) resumePending = true;
                PersistStatus();
                return !attemptActive;
            }
        }

        public void StopAttempt()
        {
            lock (gate)
            {
                if (state == "cancelled") return;
                foreach (var file in progress.Values.Where(file => file.State == "running")) RequeueFileCore(file);
                if (aggregateState == "running") aggregateState = "queued";
                if (state == "running") state = "queued";
                FinishedAt = null;
                PersistStatus();
            }
        }

        public bool EndAttempt()
        {
            lock (gate)
            {
                attemptActive = false;
                var enqueue = resumePending && state == "queued";
                resumePending = false;
                return enqueue;
            }
        }

        public ReviewRunResponse Snapshot()
        {
            lock (gate)
            {
                var files = manifest.Targets.Select(target => progress[target.Path])
                    .Select(file => new ReviewFileProgress(file.Path, file.State, file.StartedAt, file.FinishedAt, file.Error))
                    .ToArray();
                return new ReviewRunResponse(
                    Id, Repository.Id, Node.Path, manifest.Level, Kind, Model, ThinkingLevel, CliType, state,
                    files.Length,
                    files.Count(file => IsCompletedFileState(file.State)),
                    files.Count(file => file.State == "failed"),
                    CreatedAt, StartedAt, FinishedAt, files, errors.ToArray(), usageOperations, usage,
                    manifest.Estimate, tokenCap, costCap, costSpent, currency, priceStatus,
                    files.Count(file => file.State is "skipped" or "skipped-fresh"),
                    aggregateState, stopReason, Deviation(), manifest.Recommendation, manifest.RouteOverride,
                    preflightState,
                    preflight?.Results.Count ?? 0,
                    preflight?.Results.Count(result => result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed) ?? 0,
                    preflight?.ResultHash,
                    preflightDurationMs,
                    files.Count(file => file.State == "blocked-preflight"),
                    Economy());
            }
        }

        public ReviewRunStatus DurableStatus()
        {
            lock (gate) return DurableStatusCore();
        }

        private void RequeueFileCore(MutableFileProgress file)
        {
            file.State = "queued";
            file.StartedAt = null;
            file.FinishedAt = null;
            file.Error = null;
            Append(file);
        }

        private void Append(MutableFileProgress file)
        {
            AppendProgress(file);
            PersistStatus();
        }

        private void AppendProgress(MutableFileProgress file) => store.AppendProgress(
            new ReviewRunFileTransition(file.Path, file.State, file.StartedAt, file.FinishedAt, Id, file.Error));

        private void PersistStatus()
        {
            var status = DurableStatusCore();
            store.WriteStatus(status);
            store.WriteResult(manifest, status);
        }

        private ReviewRunStatus DurableStatusCore()
        {
            var ordered = manifest.Targets.Select(target => progress[target.Path]).ToArray();
            var completed = ordered.Count(file => IsCompletedFileState(file.State));
            var cursor = 0;
            while (cursor < ordered.Length && IsCompletedFileState(ordered[cursor].State)) cursor++;
            return new ReviewRunStatus(
                Id, state, ordered.Length, completed, ordered.Count(file => file.State == "failed"), cursor,
                CreatedAt, StartedAt, FinishedAt, errors.ToArray(), usageOperations, usage,
                tokenCap, costCap, costSpent, currency, priceStatus,
                ordered.Count(file => file.State is "skipped" or "skipped-fresh"),
                aggregateState, stopReason,
                preflightState,
                preflight?.Results.Count ?? 0,
                preflight?.Results.Count(result => result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed) ?? 0,
                preflight?.ResultHash,
                preflightDurationMs,
                ordered.Count(file => file.State == "blocked-preflight"),
                preflightCacheHits,
                preflight?.Results.Sum(result => result.Findings.Count) ?? 0);
        }

        private ReviewRunEconomyEvidence Economy()
        {
            var blockedFiles = progress.Values.Count(file => file.State == "blocked-preflight");
            var aggregateBlocked = aggregateState == "blocked-preflight" ? 1 : 0;
            return new ReviewRunEconomyEvidence(
                preflightDurationMs ?? 0,
                preflightCacheHits,
                preflight?.Results.Sum(result => result.Findings.Count) ?? 0,
                manifest.Estimate?.Operations ?? progress.Count + (Node.Level == ReviewLevel.File ? 0 : 1),
                blockedFiles + aggregateBlocked,
                usageOperations,
                manifest.Estimate?.PromptCharactersBeforeCompaction,
                manifest.Estimate?.PromptCharacters,
                usage,
                Deviation());
        }

        private static bool IsCompletedFileState(string fileState) =>
            fileState is "done" or "failed" or "skipped-fresh" or "blocked-preflight";

        private bool CapReached() =>
            tokenCap.HasValue && ConsumedTokens() >= tokenCap.Value ||
            costCap.HasValue && usageOperations > 0 &&
            (costSpent is null || costSpent.Value >= costCap.Value);

        private long ConsumedTokens() => Math.Max(0, usage.InputTokens ?? 0) + Math.Max(0, usage.OutputTokens ?? 0);

        private ReviewEstimateDeviation? Deviation()
        {
            if (state != "done" || manifest.Estimate is null || usage.InputTokens is null || usage.OutputTokens is null)
                return null;
            return new ReviewEstimateDeviation(
                Percent(usage.InputTokens.Value, manifest.Estimate.InputTokens),
                Percent(usage.OutputTokens.Value, manifest.Estimate.OutputTokens),
                costSpent.HasValue && manifest.Estimate.Cost.HasValue
                    ? Percent(costSpent.Value, manifest.Estimate.Cost.Value)
                    : null,
                "Positive means actual was above preflight; prompt tokenizer, CLI context, caching, and response length cause deviation.");
        }

        private static decimal Percent(decimal actual, decimal estimate) =>
            estimate == 0 ? 0 : Math.Round((actual - estimate) / estimate * 100m, 2);

        private static long? Add(long? left, long? right) =>
            left.HasValue || right.HasValue ? (left ?? 0) + (right ?? 0) : null;

        private sealed class MutableFileProgress(string path)
        {
            public string Path { get; } = path;
            public string State { get; set; } = "queued";
            public DateTimeOffset? StartedAt { get; set; }
            public DateTimeOffset? FinishedAt { get; set; }
            public string? Error { get; set; }
        }
    }
}
