using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Populates the existing immutable repository snapshots before an operator switches to them.</summary>
public sealed class RepositorySnapshotPrewarmer : BackgroundService
{
    private readonly Channel<RepositoryRegistration> queue = Channel.CreateUnbounded<RepositoryRegistration>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> pending = new(StringComparer.Ordinal);
    private readonly RepositoryRegistry registry;
    private readonly RepositoryHierarchyCache hierarchyCache;
    private readonly ProjectDashboardService dashboards;
    private readonly InputResolver inputResolver;
    private readonly ILogger<RepositorySnapshotPrewarmer> logger;

    public RepositorySnapshotPrewarmer(
        RepositoryRegistry registry,
        RepositoryHierarchyCache hierarchyCache,
        ProjectDashboardService dashboards,
        InputResolver inputResolver,
        ILogger<RepositorySnapshotPrewarmer> logger)
    {
        this.registry = registry;
        this.hierarchyCache = hierarchyCache;
        this.dashboards = dashboards;
        this.inputResolver = inputResolver;
        this.logger = logger;
    }

    public void QueueAll(IEnumerable<RepositoryRegistration> registrations)
    {
        foreach (var registration in registrations) Queue(registration);
    }

    public void Queue(RepositoryRegistration registration)
    {
        if (registration.Archived) return;
        var key = string.Join('\0', registration.RootPath, registration.GlobalInputsDirectory,
            registration.InputBudgetCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!pending.TryAdd(key, 0)) return;
        if (!queue.Writer.TryWrite(registration)) pending.TryRemove(key, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep host startup non-blocking: the API becomes reachable while snapshots warm in the background.
        await Task.Yield();
        QueueAll(registry.List());
        await foreach (var registration in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var key = string.Join('\0', registration.RootPath, registration.GlobalInputsDirectory,
                registration.InputBudgetCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                var started = Stopwatch.GetTimestamp();
                var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
                    ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
                    : registration.GlobalInputsDirectory;
                var hierarchy = await Task.Run(() => hierarchyCache.GetMeasured(
                    registration.RootPath,
                    inputResolver,
                    globalDirectory,
                    registration.InputBudgetCharacters), stoppingToken);
                var projection = dashboards.GetMeasured(registration.RootPath, hierarchy.Snapshot);
                var prewarmEvent = JsonSerializer.Serialize(new
                {
                    @event = "qs.repository.prewarm",
                    repositoryId = registration.Id,
                    cache = hierarchy.CacheHit && projection.CacheHit ? "warm" : "cold",
                    durationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2),
                    phases = new
                    {
                        gitStatusMs = Math.Round(hierarchy.GitStatusMilliseconds, 2),
                        scanMs = Math.Round(hierarchy.ScanMilliseconds, 2),
                        reviewMetaDiscoveryMs = Math.Round(hierarchy.ReviewMetaDiscoveryMilliseconds, 2),
                        projectionMs = Math.Round(projection.ProjectionMilliseconds, 2),
                    },
                    fileCount = projection.Dashboard.Metrics.FileCount,
                });
                logger.LogInformation(new EventId(1112, "RepositoryPrewarmed"), "{RepositoryPrewarmEvent}", prewarmEvent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(new EventId(1113, "RepositoryPrewarmFailed"), exception,
                    "Repository snapshot prewarm failed for {RepositoryId}", registration.Id);
            }
            finally
            {
                pending.TryRemove(key, out _);
            }
        }
    }
}
