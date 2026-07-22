using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record StartReviewRequest(string Path, string Kind, string? Model = null, string? CliType = null);

public sealed record ReviewFileProgress(string Path, string State, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error);

public sealed record ReviewRunResponse(
    string Id,
    string RepositoryId,
    string Path,
    string Level,
    string Kind,
    string? Model,
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
    TokenUsage Usage);

public sealed class ReviewJobsOptions
{
    public const string SectionName = "ReviewJobs";
    public int MaxConcurrency { get; set; } = 2;
    public int RecentRunLimit { get; set; } = 30;
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

    public ReviewJobService(RepositoryRegistry repositories, IOptions<ReviewJobsOptions> options,
        ILogger<ReviewJobService> logger, QuotaService quotas, RepositoryHierarchyCache hierarchyCache)
    {
        this.repositories = repositories;
        this.options = options.Value;
        this.logger = logger;
        this.quotas = quotas;
        this.hierarchyCache = hierarchyCache;
    }

    public async Task<ReviewRunResponse> EnqueueAsync(
        string repositoryId,
        StartReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
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
            string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            string.IsNullOrWhiteSpace(request.CliType) ? "codex" : request.CliType.Trim(),
            DateTimeOffset.UtcNow,
            targets,
            AggregateControls(node));
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
            "Queued review {ReviewRunId} for {RepositoryId}:{ReviewPath} ({ReviewLevel}, {ReviewKind}, {FileCount} files) in {ElapsedMilliseconds} ms",
            item.Id, registration.Id, node.Path, node.Level, item.Kind, files.Length, stopwatch.ElapsedMilliseconds);
        return item.Snapshot();
    }

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

    public ReviewRunResponse Resume(string repositoryId, string id)
    {
        var run = Find(repositoryId, id);
        if (run.Resume() && !queue.Writer.TryWrite(run))
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
        logger.LogInformation(new EventId(1501, "ReviewStarted"), "Started review {ReviewRunId}", item.Id);
        try
        {
            await Parallel.ForEachAsync(item.PendingFiles(),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrency, 1, 16),
                    CancellationToken = linked.Token,
                },
                async (file, cancellationToken) =>
                {
                    if (!item.StartFile(file.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return;
                    }
                    try
                    {
                        await CreateRunner(item).ReviewAsync(
                            CreateRequest(item, file, ReviewLevel.File, [file.Path]), cancellationToken);
                        item.FinishFile(file.Path, null);
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
                }).ConfigureAwait(false);

            if (item.Node.Level != ReviewLevel.File)
            {
                await CreateRunner(item).ReviewAsync(
                    CreateRequest(item, item.Node, item.Node.Level, item.Files.Select(file => file.Path).ToArray()), linked.Token);
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

    private ReviewWorkItem Find(string repositoryId, string id)
    {
        if (!runs.TryGetValue(id, out var run) ||
            !string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Review run '{id}' was not found.");
        return run;
    }

    private ReviewRunner CreateRunner(ReviewWorkItem item) =>
        new(new CodingAgentReviewAgent(item.CliType, item.Model,
                eventObserver: (_, runEvent) => quotas.Observe(item.CliType, runEvent)),
            usageRecorded: usage => item.AddUsage(usage.Tokens));

    private static ReviewRequest CreateRequest(
        ReviewWorkItem item,
        HierarchyNode node,
        ReviewLevel level,
        IReadOnlyList<string> files) =>
        new(node.Path, item.Kind, level,
            RepositoryRoot: item.Repository.RootPath,
            GlobalInputsDirectory: item.Repository.GlobalInputsDirectory,
            InputBudgetCharacters: item.Repository.InputBudgetCharacters,
            UnitId: node.Id,
            SubjectFiles: files,
            DisplayName: node.Name,
            SubjectUnits: level == ReviewLevel.File
                ? null
                : item.Files.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
            AggregateControls: item.AggregateControls);

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
        private TokenUsage usage;
        private CancellationTokenSource attemptCancellation = new();
        private int usageOperations;
        private bool attemptActive;
        private bool resumePending;
        private string state;

        private ReviewWorkItem(
            ReviewRunManifest manifest,
            RepositoryRegistration repository,
            ReviewRunStore store,
            ReviewRunStatus? status,
            IReadOnlyList<ReviewRunFileTransition>? transitions)
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
            if (transitions is not null)
            {
                foreach (var transition in transitions)
                {
                    if (!progress.TryGetValue(transition.Path, out var file) ||
                        transition.State is not ("queued" or "running" or "done" or "failed" or "cancelled")) continue;
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
            ReviewRunStore store) => new(manifest, repository, store, null, null);

        public static ReviewWorkItem Restore(
            StoredReviewRun stored,
            RepositoryRegistration repository,
            ReviewRunStore store) => new(stored.Manifest, repository, store, stored.Status, stored.Progress);

        public string Id => manifest.RunId;
        public RepositoryRegistration Repository { get; }
        public HierarchyNode Node { get; }
        public IReadOnlyList<HierarchyNode> Files { get; }
        public IReadOnlyList<string>? AggregateControls => manifest.AggregateControls;
        public string Kind => manifest.Kind;
        public string? Model => manifest.Model;
        public string CliType => manifest.CliType;
        public DateTimeOffset CreatedAt => manifest.CreatedAt;
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public string State { get { lock (gate) return state; } }
        public int FailedFiles { get { lock (gate) return progress.Values.Count(file => file.State == "failed"); } }

        public void PrepareForRecovery()
        {
            lock (gate)
            {
                if (ReviewRunStore.IsTerminal(state)) return;
                foreach (var file in progress.Values.Where(file => file.State == "running")) RequeueFileCore(file);
                state = state == "paused" ? "paused" : "queued";
                FinishedAt = null;
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

        public void AddUsage(TokenUsage operationUsage)
        {
            lock (gate)
            {
                usageOperations++;
                usage = new TokenUsage(
                    Add(usage.InputTokens, operationUsage.InputTokens),
                    Add(usage.OutputTokens, operationUsage.OutputTokens),
                    Add(usage.CachedInputTokens, operationUsage.CachedInputTokens),
                    Add(usage.ReasoningOutputTokens, operationUsage.ReasoningOutputTokens),
                    usage.DurationMs + operationUsage.DurationMs);
                PersistStatus();
            }
        }

        public bool Complete()
        {
            lock (gate)
            {
                if (state != "running") return false;
                state = "done";
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

        public bool Resume()
        {
            lock (gate)
            {
                if (state != "paused") throw new ArgumentException($"Review '{Id}' is not paused.");
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
                    Id, Repository.Id, Node.Path, manifest.Level, Kind, Model, CliType, state,
                    files.Length,
                    files.Count(file => file.State is "done" or "failed"),
                    files.Count(file => file.State == "failed"),
                    CreatedAt, StartedAt, FinishedAt, files, errors.ToArray(), usageOperations, usage);
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

        private void PersistStatus() => store.WriteStatus(DurableStatusCore());

        private ReviewRunStatus DurableStatusCore()
        {
            var ordered = manifest.Targets.Select(target => progress[target.Path]).ToArray();
            var completed = ordered.Count(file => file.State is "done" or "failed");
            var cursor = 0;
            while (cursor < ordered.Length && ordered[cursor].State is "done" or "failed") cursor++;
            return new ReviewRunStatus(
                Id, state, ordered.Length, completed, ordered.Count(file => file.State == "failed"), cursor,
                CreatedAt, StartedAt, FinishedAt, errors.ToArray(), usageOperations, usage);
        }

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
