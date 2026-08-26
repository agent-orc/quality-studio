using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;
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
    bool OverrideBelowFloor);

public sealed record ReviewEstimateDeviation(
    decimal InputTokensPercent,
    decimal OutputTokensPercent,
    decimal? CostPercent,
    string Note);

public sealed record ReviewFileProgress(string Path, string State, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error);

public sealed record ReviewRunFailure(
    string Code,
    string Stage,
    string Message,
    string? Operation,
    string CliType,
    string? ReviewerRunId = null,
    string? ConfiguredExecutable = null,
    string? LogDirectory = null);

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
    ReviewRunFailure? Failure);

public interface IReviewExecutor
{
    Task<ReviewExecutionResult> ReviewIfNeededAsync(
        ReviewRequest request,
        bool force,
        CancellationToken cancellationToken);
}

/// <summary>Marks an executor that must emit a process-start event before it can do review work.</summary>
public interface IReviewProcessExecutor;

public interface IReviewExecutorFactory
{
    IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
        Action<ReviewUsageEntry> usageRecorded);
}

public sealed class ReviewExecutorFactory(
    SensorRegistry sensors,
    StalenessEvaluator stalenessEvaluator,
    IOptions<ReviewJobsOptions> options,
    ILoggerFactory loggerFactory) : IReviewExecutorFactory
{
    public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
        Action<ReviewUsageEntry> usageRecorded)
    {
        var cliOptions = new CliOptions
        {
            ClaudePath = string.IsNullOrWhiteSpace(options.Value.ClaudePath) ? null : options.Value.ClaudePath,
            // Review prompts routinely exceed Windows' CreateProcess command-line limit.
            // Stdin also keeps repository content out of process listings.
            ClaudePromptTransport = ClaudePromptTransport.Stdin,
        };
        return new ReviewExecutor(new ReviewRunner(new CodingAgentReviewAgent(
                cliType, model, thinkingLevel, cliOptions, eventObserver,
                loggerFactory.CreateLogger<CodingAgentReviewAgent>()),
            usageRecorded: usageRecorded, sensorRegistry: sensors, stalenessEvaluator: stalenessEvaluator));
    }

    private sealed class ReviewExecutor(ReviewRunner runner) : IReviewExecutor, IReviewProcessExecutor
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
    public TimeSpan ReviewerAttachTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    public string? ClaudePath { get; set; }
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
        var (registration, access, node, files) = plan;
        var selection = modelCatalog.Resolve(request.CliType, request.Model, request.ThinkingLevel);
        var cliType = selection.CliType;
        var model = selection.Model;
        var recommendation = modelCatalog.Recommend(request.Kind, node.Level, files.Length);
        var belowFloor = modelCatalog.IsBelowCorrectnessFloor(selection, recommendation);
        if (belowFloor && !request.ConfirmBelowFloor)
            throw new ArgumentException(
                $"The explicit route is below the {recommendation.CorrectnessFloor} correctness floor. Confirm the below-floor override before starting.");
        var (tokenCap, costCap) = ResolveCap(registration, request.TokenCap, request.CostCap);
        var estimate = await EstimateAsync(plan, request.Kind, cliType, model, request.Force, cancellationToken).ConfigureAwait(false);
        if (costCap.HasValue && estimate.Cost is null)
            throw new ArgumentException($"A cost cap cannot be enforced because model '{model ?? "runner-default"}' has no price in the runner catalogue. Use a token cap instead.");

        var runId = "review-" + Guid.NewGuid().ToString("N");
        var targets = new List<ReviewRunPlanTarget>(files.Length);
        foreach (var file in files)
        {
            var subjectHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(
                access.ResolveFile(file.Path), cancellationToken).ConfigureAwait(false);
            targets.Add(new ReviewRunPlanTarget(file.Id, file.Name, file.Path, subjectHash));
        }

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
        var item = ReviewWorkItem.Create(manifest, registration, store);
        store.Create(manifest, item.DurableStatus());
        runs[item.Id] = item;
        if (!queue.Writer.TryWrite(item))
        {
            item.Fail("The review queue is unavailable.");
            throw new InvalidOperationException("The review queue is unavailable.");
        }
        logger.LogInformation(new EventId(1500, "ReviewQueued"),
            "Queued review {ReviewRunId} for {RepositoryId}:{ReviewPath} ({ReviewLevel}, {ReviewKind}, {FileCount} files) via {ReviewCli}/{ReviewModel}/{ReviewThinkingLevel} in {ElapsedMilliseconds} ms",
            item.Id, registration.Id, node.Path, node.Level, item.Kind, files.Length, item.CliType,
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
        var estimate = await EstimateAsync(plan, request.Kind, cliType, model, request.Force, cancellationToken).ConfigureAwait(false);
        if (costCap.HasValue && estimate.Cost is null)
            throw new ArgumentException($"A cost cap cannot be enforced because model '{model ?? "runner-default"}' has no price in the runner catalogue. Use a token cap instead.");
        return new ReviewPreflightResponse(plan.Registration.Id, plan.Node.Path,
            plan.Node.Level.ToString().ToLowerInvariant(), request.Kind, model, selection.ThinkingLevel,
            cliType, estimate, tokenCap, costCap, recommendation, belowFloor);
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
        PreparedPlan plan, string kind, string cliType, string? model, bool force, CancellationToken cancellationToken)
    {
        var promptRunner = new ReviewRunner();
        var measurements = new List<ReviewPromptMeasurement>(plan.Files.Length + 1);
        foreach (var file in plan.Files)
        {
            measurements.Add(await promptRunner.MeasurePromptAsync(
                CreateEstimateRequest(plan, file, ReviewLevel.File, [file.Path], kind), cancellationToken)
                .ConfigureAwait(false));
        }
        if (plan.Node.Level != ReviewLevel.File)
        {
            measurements.Add(await promptRunner.MeasurePromptAsync(
                CreateEstimateRequest(plan, plan.Node, plan.Node.Level,
                    plan.Files.Select(file => file.Path).ToArray(), kind), cancellationToken).ConfigureAwait(false));
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
        return new ReviewRunEstimate(plan.Files.Length, measurements.Count, promptCharacters, inputTokens,
            outputTokens, cost.Total, cost.Currency, Camel(cost.Status.ToString()), samples.Length,
            samples.Length == 0
                ? "Input is actual rendered prompt characters / 4; output uses a 20% fallback ratio."
                : $"Input is actual rendered prompt characters / 4; output uses {samples.Length} recorded .quality/usage operation(s).",
            expectedFreshSkips
        );
    }

    private static ReviewRequest CreateEstimateRequest(
        PreparedPlan plan, HierarchyNode node, ReviewLevel level, IReadOnlyList<string> files, string kind) =>
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
            AggregateExclusions: level == ReviewLevel.File ? null : plan.Node.Exclusions);

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
                    item.ReclaimTerminalFiles();
                    item.EnsureTerminalReport();
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
            item.DeterministicEvidence = await new DeterministicEvidenceCollector(sensorRegistry)
                .CollectAsync(
                    item.Repository.RootPath,
                    (item.Repository.Sensors ?? [])
                        .Where(sensor => sensor.Enabled)
                        .Select(sensor => new ReviewSensorConfiguration(sensor.Id, sensor.Configuration))
                        .ToArray(),
                    linked.Token)
                .ConfigureAwait(false);
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
                    if (item.StartAggregate())
                    {
                        var execution = await ExecuteReviewOperationAsync(
                            item,
                            QualityRunReportFactory.AggregateOperationId,
                            CreateRequest(item, item.Node, item.Node.Level, item.Files.Select(file => file.Path).ToArray()),
                            linked.Token).ConfigureAwait(false);
                        item.FinishAggregate(execution);
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
            item.Fail(FailureFromException(item, null, exception));
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
        if (!item.StartFile(file.Path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        try
        {
            var execution = await ExecuteReviewOperationAsync(
                item,
                file.Path,
                CreateRequest(item, file, ReviewLevel.File, [file.Path]),
                cancellationToken).ConfigureAwait(false);
            item.FinishFile(file.Path, execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (item.State == "cancelled") item.CancelFile(file.Path); else item.RequeueFile(file.Path);
            throw;
        }
        catch (Exception exception) when (exception is ReviewOperationWatchdogException or ReviewAgentRunException)
        {
            item.Fail(FailureFromException(item, file.Path, exception));
            logger.LogError(new EventId(1513, "ReviewInfrastructureFailed"), exception,
                "Reviewer infrastructure failed for file {ReviewFilePath} in review {ReviewRunId}", file.Path, item.Id);
            throw;
        }
        catch (Exception exception)
        {
            item.FailFile(file.Path, exception.Message);
            logger.LogError(new EventId(1504, "ReviewFileFailed"), exception,
                "File {ReviewFilePath} failed in review {ReviewRunId}", file.Path, item.Id);
        }
    }

    private ReviewWorkItem Find(string repositoryId, string id)
    {
        if (!runs.TryGetValue(id, out var run) ||
            !string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Review run '{id}' was not found.");
        return run;
    }

    private ReviewOperation CreateOperation(ReviewWorkItem item)
    {
        var attached = new TaskCompletionSource<CliRunEvent.RunStarted>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = executors.Create(
            item.CliType, item.Model, item.ThinkingLevel,
            (_, runEvent) =>
            {
                quotas.Observe(item.CliType, runEvent);
                if (runEvent is CliRunEvent.RunStarted started) attached.TrySetResult(started);
            },
            item.AddUsage);
        return new ReviewOperation(executor, executor is IReviewProcessExecutor ? attached.Task : null);
    }

    private async Task<ReviewExecutionResult> ExecuteReviewOperationAsync(
        ReviewWorkItem item,
        string operation,
        ReviewRequest request,
        CancellationToken cancellationToken)
    {
        var reviewOperation = CreateOperation(item);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var execution = reviewOperation.Executor.ReviewIfNeededAsync(request, item.Force, operationCancellation.Token);

        if (reviewOperation.ProcessAttached is not null)
        {
            var attachTimeout = Positive(options.ReviewerAttachTimeout, TimeSpan.FromSeconds(30));
            var attachDeadline = Task.Delay(attachTimeout, cancellationToken);
            var first = await Task.WhenAny(execution, reviewOperation.ProcessAttached, attachDeadline).ConfigureAwait(false);
            if (first == attachDeadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    operationCancellation.Cancel();
                    await AwaitCancellationGraceAsync(item, operation, execution).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await ReclaimOperationAsync(item, operation, execution, operationCancellation,
                    "reviewer-attach-timeout", "process-attach",
                    $"The {item.CliType} reviewer did not attach a process within {attachTimeout.TotalSeconds:0.###} seconds.")
                    .ConfigureAwait(false);
            }
        }

        try
        {
            return await execution.WaitAsync(
                Positive(options.OperationTimeout, TimeSpan.FromMinutes(15)), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var timeout = Positive(options.OperationTimeout, TimeSpan.FromMinutes(15));
            await ReclaimOperationAsync(item, operation, execution, operationCancellation,
                "reviewer-operation-timeout", "operation",
                $"The {item.CliType} reviewer exceeded the {timeout.TotalSeconds:0.###}-second operation timeout.")
                .ConfigureAwait(false);
            throw new UnreachableException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operationCancellation.Cancel();
            await AwaitCancellationGraceAsync(item, operation, execution).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReclaimOperationAsync(
        ReviewWorkItem item,
        string operation,
        Task execution,
        CancellationTokenSource operationCancellation,
        string code,
        string stage,
        string message)
    {
        operationCancellation.Cancel();
        await AwaitCancellationGraceAsync(item, operation, execution).ConfigureAwait(false);
        throw new ReviewOperationWatchdogException(new ReviewRunFailure(
            code, stage, message, operation, item.CliType));
    }

    private async Task AwaitCancellationGraceAsync(ReviewWorkItem item, string operation, Task execution)
    {
        try
        {
            await execution.WaitAsync(
                Positive(options.CancellationGracePeriod, TimeSpan.FromSeconds(5)), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = execution.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            logger.LogCritical(new EventId(1514, "ReviewerReclaimedAfterCancellationGrace"),
                "Reclaimed review {ReviewRunId} operation {ReviewOperation} after its reviewer ignored cancellation; the queue will continue",
                item.Id, operation);
        }
        catch (OperationCanceledException)
        {
            // Expected: the reviewer observed the cancellation request during the grace period.
        }
        catch
        {
            // The caller owns the typed timeout/cancellation outcome. The executor task is observed here.
        }
    }

    private static TimeSpan Positive(TimeSpan configured, TimeSpan fallback) =>
        configured > TimeSpan.Zero ? configured : fallback;

    private static ReviewRunFailure FailureFromException(
        ReviewWorkItem item,
        string? operation,
        Exception exception)
    {
        if (exception is ReviewOperationWatchdogException watchdog) return watchdog.Failure;
        if (exception is ReviewAgentRunException agent)
        {
            return new ReviewRunFailure(
                agent.Code, agent.Stage, agent.Message, operation, item.CliType,
                agent.RunId, agent.ConfiguredExecutable, agent.LogDirectory);
        }
        return new ReviewRunFailure(
            "review-run-failed", "run", exception.Message, operation, item.CliType);
    }

    private sealed record ReviewOperation(IReviewExecutor Executor, Task<CliRunEvent.RunStarted>? ProcessAttached);

    private sealed class ReviewOperationWatchdogException(ReviewRunFailure failure) : TimeoutException(failure.Message)
    {
        public ReviewRunFailure Failure { get; } = failure;
    }

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
                ? (item.Repository.Sensors ?? Array.Empty<RepositorySensorConfiguration>())
                    .Where(sensor => sensor.Enabled &&
                                     sensorRegistry.Get(sensor.Id) is not IDeterministicEvidenceSensor)
                    .Select(sensor => new ReviewSensorConfiguration(sensor.Id, sensor.Configuration))
                    .ToArray()
                : null,
            DeterministicEvidence: item.DeterministicEvidence);
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
        private readonly Dictionary<string, ReviewObservationSnapshot> observations;
        private readonly QualityRunReportStore reportStore;
        private readonly List<string> errors;
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
        private ReviewRunFailure? failure;
        private bool attemptActive;
        private bool resumePending;
        private string state;
        private int reportRevision;

        private ReviewWorkItem(
            ReviewRunManifest manifest,
            RepositoryRegistration repository,
            ReviewRunStore store,
            ReviewRunStatus? status,
            IReadOnlyList<ReviewRunFileTransition>? transitions,
            IReadOnlyDictionary<string, ReviewObservationSnapshot>? storedObservations)
        {
            this.manifest = manifest;
            this.store = store;
            Repository = repository;
            reportStore = new QualityRunReportStore(repository.RootPath);
            observations = storedObservations?.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal) ?? new Dictionary<string, ReviewObservationSnapshot>(StringComparer.Ordinal);
            reportRevision = reportStore.TryLoad(manifest.RunId, out var existingReport)
                ? existingReport!.Run.Revision
                : 0;
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
            failure = status?.Failure;
            if (transitions is not null)
            {
                foreach (var transition in transitions)
                {
                    if (!progress.TryGetValue(transition.Path, out var file) ||
                        transition.State is not ("queued" or "running" or "done" or "failed" or "cancelled" or "skipped" or "skipped-fresh")) continue;
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
            ReviewRunStore store) => new(manifest, repository, store, null, null, null);

        public static ReviewWorkItem Restore(
            StoredReviewRun stored,
            RepositoryRegistration repository,
            ReviewRunStore store) => new(stored.Manifest, repository, store, stored.Status, stored.Progress,
                stored.Observations);

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

        public void PrepareForRecovery()
        {
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                foreach (var file in progress.Values.Where(file => file.State == "running")) RequeueFileCore(file);
                if (aggregateState == "running") aggregateState = "queued";
                state = state == "paused" ? "paused" : "queued";
                FinishedAt = null;
                PersistStatus();
            }
        }

        public void ReclaimTerminalFiles()
        {
            lock (gate)
            {
                if (state is not ("cancelled" or "failed")) return;
                var replacement = state == "cancelled" ? "cancelled" : "failed";
                var changed = false;
                foreach (var file in progress.Values.Where(file => file.State is "queued" or "running"))
                {
                    file.State = replacement;
                    file.FinishedAt = FinishedAt ?? DateTimeOffset.UtcNow;
                    file.Error = state == "failed" ? failure?.Message ?? errors.FirstOrDefault() : null;
                    AppendProgress(file);
                    changed = true;
                }
                if (aggregateState is "queued" or "running")
                {
                    aggregateState = replacement;
                    changed = true;
                }
                if (changed) PersistStatus();
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

        public void FinishFile(string path, ReviewExecutionResult execution)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State != "running") return;
                CaptureObservation(path, execution.Observation);
                file.State = execution.SkippedFresh ? "skipped-fresh" : "done";
                file.Error = null;
                file.FinishedAt = DateTimeOffset.UtcNow;
                Append(file);
            }
        }

        public void FailFile(string path, string error)
        {
            lock (gate)
            {
                var file = progress[path];
                if (file.State != "running") return;
                file.State = "failed";
                file.Error = error;
                file.FinishedAt = DateTimeOffset.UtcNow;
                errors.Add($"{path}: {error}");
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

        public void FinishAggregate(ReviewExecutionResult execution)
        {
            lock (gate)
            {
                if (aggregateState != "running") return;
                CaptureObservation(QualityRunReportFactory.AggregateOperationId, execution.Observation);
                aggregateState = execution.SkippedFresh ? "skipped-fresh" : "done";
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
                PublishReport();
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
                PublishReport();
                return true;
            }
        }

        public void Fail(string error) => Fail(new ReviewRunFailure(
            "review-run-failed", "run", error, null, CliType));

        public void Fail(ReviewRunFailure runFailure)
        {
            CancellationTokenSource? cancellation;
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                state = "failed";
                failure = runFailure;
                if (aggregateState is "queued" or "running") aggregateState = "failed";
                errors.Add(runFailure.Message);
                FinishedAt = DateTimeOffset.UtcNow;
                foreach (var file in progress.Values.Where(file => file.State is "queued" or "running"))
                {
                    file.State = "failed";
                    file.Error = runFailure.Message;
                    file.FinishedAt = FinishedAt;
                    AppendProgress(file);
                }
                PersistStatus();
                PublishReport();
                cancellation = attemptCancellation;
            }
            cancellation.Cancel();
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
                PublishReport();
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
                    aggregateState, stopReason, Deviation(), manifest.Recommendation, manifest.RouteOverride, failure);
            }
        }

        public ReviewRunStatus DurableStatus()
        {
            lock (gate) return DurableStatusCore();
        }

        public void EnsureTerminalReport()
        {
            lock (gate)
            {
                if (!ReviewRunStore.IsTerminal(state) || reportStore.TryLoad(Id, out _)) return;
                PublishReport();
            }
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

        private void CaptureObservation(string operationId, ReviewObservationSnapshot? snapshot)
        {
            if (snapshot is null) return;
            // The durable observation write deliberately precedes the progress transition.
            store.WriteObservation(Id, operationId, snapshot);
            observations[operationId] = snapshot;
        }

        private void PersistStatus()
        {
            var status = DurableStatusCore();
            store.WriteStatus(status);
            store.WriteResult(manifest, status);
        }

        private void PublishReport()
        {
            var status = DurableStatusCore();
            var latest = manifest.Targets.Select(target => progress[target.Path])
                .Select(file => new ReviewRunFileTransition(
                    file.Path, file.State, file.StartedAt, file.FinishedAt, Id, file.Error)).ToArray();
            var report = QualityRunReportFactory.Build(
                manifest,
                status,
                latest,
                observations,
                Repository.RootPath,
                Repository.DisplayName,
                reportRevision + 1,
                reportStore.LoadAll());
            reportStore.Save(report);
            reportRevision = report.Run.Revision;
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
                aggregateState, stopReason, failure);
        }

        private static bool IsCompletedFileState(string fileState) =>
            fileState is "done" or "failed" or "skipped-fresh";

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
