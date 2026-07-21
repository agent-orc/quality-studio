using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using AgentOrchestrator.CodeQuality;
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
    IReadOnlyList<string> Errors);

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

    public ReviewJobService(RepositoryRegistry repositories, IOptions<ReviewJobsOptions> options, ILogger<ReviewJobService> logger)
    {
        this.repositories = repositories;
        this.options = options.Value;
        this.logger = logger;
    }

    public ReviewRunResponse Enqueue(string repositoryId, StartReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path)) throw new ArgumentException("A hierarchy path is required.");
        if (!Kinds.Contains(request.Kind)) throw new ArgumentException("Kind must be code, security, or performance.");
        var registration = repositories.Get(repositoryId);
        if (!registration.EnabledReviewKinds.Contains(request.Kind, StringComparer.Ordinal))
            throw new ArgumentException($"Review kind '{request.Kind}' is not enabled for this repository.");

        var access = new RepositoryAccess(registration.RootPath);
        var path = access.NormalizeRelativePath(request.Path);
        var hierarchy = RepositoryHierarchyBuilder.BuildDotNet(registration.RootPath);
        var node = Flatten(hierarchy).FirstOrDefault(candidate =>
            candidate.Level != ReviewLevel.Function && string.Equals(candidate.Path, path, StringComparison.Ordinal));
        if (node is null) throw new KeyNotFoundException($"No reviewable hierarchy node exists at '{path}'.");
        var files = node.Level == ReviewLevel.File
            ? [node]
            : Flatten([node]).Where(candidate => candidate.Level == ReviewLevel.File)
                .DistinctBy(candidate => candidate.Path, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new ArgumentException("The selected node has no reviewable descendant files.");

        var item = new ReviewWorkItem(
            "review-" + Guid.NewGuid().ToString("N"), registration, node, files,
            request.Kind, string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            string.IsNullOrWhiteSpace(request.CliType) ? "codex" : request.CliType.Trim());
        runs[item.Id] = item;
        if (!queue.Writer.TryWrite(item)) throw new InvalidOperationException("The review queue is unavailable.");
        logger.LogInformation(new EventId(1500, "ReviewQueued"),
            "Queued review {ReviewRunId} for {RepositoryId}:{ReviewPath} ({ReviewLevel}, {ReviewKind}, {FileCount} files)",
            item.Id, registration.Id, node.Path, node.Level, item.Kind, files.Length);
        return item.Snapshot();
    }

    public IReadOnlyList<ReviewRunResponse> List(string repositoryId) => runs.Values
        .Where(run => string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(run => run.CreatedAt).Take(Math.Max(1, options.RecentRunLimit)).Select(run => run.Snapshot()).ToArray();

    public ReviewRunResponse Get(string repositoryId, string id)
    {
        if (!runs.TryGetValue(id, out var run) || !string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Review run '{id}' was not found.");
        return run.Snapshot();
    }

    public ReviewRunResponse Cancel(string repositoryId, string id)
    {
        if (!runs.TryGetValue(id, out var run) || !string.Equals(run.Repository.Id, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Review run '{id}' was not found.");
        run.Cancel();
        logger.LogInformation(new EventId(1503, "ReviewCancellationRequested"), "Cancellation requested for review {ReviewRunId}", id);
        return run.Snapshot();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (item.State == "cancelled") continue;
                await RunAsync(item, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(new EventId(1506, "ReviewQueueStopped"), "Review queue stopped with the API host");
        }
    }

    private async Task RunAsync(ReviewWorkItem item, CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, item.Cancellation.Token);
        item.Start();
        logger.LogInformation(new EventId(1501, "ReviewStarted"), "Started review {ReviewRunId}", item.Id);
        try
        {
            await Parallel.ForEachAsync(item.Files,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrency, 1, 16), CancellationToken = linked.Token },
                async (file, cancellationToken) =>
                {
                    item.StartFile(file.Path);
                    try
                    {
                        await CreateRunner(item).ReviewAsync(CreateRequest(item, file, ReviewLevel.File, [file.Path]), cancellationToken);
                        item.FinishFile(file.Path, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        item.CancelFile(file.Path);
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
            item.Complete();
            logger.LogInformation(new EventId(1502, "ReviewCompleted"),
                "Completed review {ReviewRunId} with {FailedFileCount} failed files in {ElapsedMilliseconds} ms",
                item.Id, item.FailedFiles, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            item.Cancel();
        }
        catch (Exception exception)
        {
            item.Fail(exception.Message);
            logger.LogError(new EventId(1505, "ReviewFailed"), exception, "Review {ReviewRunId} failed", item.Id);
        }
    }

    private static ReviewRunner CreateRunner(ReviewWorkItem item) =>
        new(new CodingAgentReviewAgent(item.CliType, item.Model));

    private static ReviewRequest CreateRequest(ReviewWorkItem item, HierarchyNode node, ReviewLevel level, IReadOnlyList<string> files) =>
        new(node.Path, item.Kind, level,
            RepositoryRoot: item.Repository.RootPath,
            GlobalInputsDirectory: item.Repository.GlobalInputsDirectory,
            InputBudgetCharacters: item.Repository.InputBudgetCharacters,
            UnitId: node.Id,
            SubjectFiles: files,
            DisplayName: node.Name,
            SubjectUnits: level == ReviewLevel.File ? null : item.Files.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
            AggregateControls: AggregateControls(node));

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
        private readonly Dictionary<string, MutableFileProgress> progress;
        private readonly List<string> errors = [];

        public ReviewWorkItem(string id, RepositoryRegistration repository, HierarchyNode node,
            IReadOnlyList<HierarchyNode> files, string kind, string? model, string cliType)
        {
            Id = id; Repository = repository; Node = node; Files = files; Kind = kind; Model = model; CliType = cliType;
            CreatedAt = DateTimeOffset.UtcNow;
            progress = files.ToDictionary(file => file.Path, file => new MutableFileProgress(file.Path), StringComparer.Ordinal);
        }

        public string Id { get; }
        public RepositoryRegistration Repository { get; }
        public HierarchyNode Node { get; }
        public IReadOnlyList<HierarchyNode> Files { get; }
        public string Kind { get; }
        public string? Model { get; }
        public string CliType { get; }
        public DateTimeOffset CreatedAt { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public string State { get; private set; } = "queued";
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public int FailedFiles { get { lock (gate) return progress.Values.Count(file => file.State == "failed"); } }

        public void Start() { lock (gate) { State = "running"; StartedAt = DateTimeOffset.UtcNow; } }
        public void StartFile(string path) { lock (gate) { progress[path].State = "running"; progress[path].StartedAt = DateTimeOffset.UtcNow; } }
        public void FinishFile(string path, string? error) { lock (gate) { var file = progress[path]; file.State = error is null ? "done" : "failed"; file.Error = error; file.FinishedAt = DateTimeOffset.UtcNow; if (error is not null) errors.Add($"{path}: {error}"); } }
        public void CancelFile(string path) { lock (gate) { var file = progress[path]; file.State = "cancelled"; file.FinishedAt = DateTimeOffset.UtcNow; } }
        public void Complete() { lock (gate) { State = "done"; FinishedAt = DateTimeOffset.UtcNow; } }
        public void Fail(string error) { lock (gate) { State = "failed"; errors.Add(error); FinishedAt = DateTimeOffset.UtcNow; } }
        public void Cancel() { lock (gate) { if (State is "done" or "failed" or "cancelled") return; State = "cancelled"; FinishedAt = DateTimeOffset.UtcNow; foreach (var file in progress.Values.Where(file => file.State is "queued" or "running")) { file.State = "cancelled"; file.FinishedAt = DateTimeOffset.UtcNow; } Cancellation.Cancel(); } }

        public ReviewRunResponse Snapshot()
        {
            lock (gate)
            {
                var files = progress.Values.Select(file => new ReviewFileProgress(file.Path, file.State, file.StartedAt, file.FinishedAt, file.Error)).ToArray();
                return new(Id, Repository.Id, Node.Path, Node.Level.ToString().ToLowerInvariant(), Kind, Model, CliType, State,
                    files.Length, files.Count(file => file.State is "done" or "failed"), files.Count(file => file.State == "failed"),
                    CreatedAt, StartedAt, FinishedAt, files, errors.ToArray());
            }
        }

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
