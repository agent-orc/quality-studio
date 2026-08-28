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
    private readonly RepositorySnapshotStore snapshotStore;
    private readonly RepositorySensorAvailabilityCache sensorAvailabilityCache;
    private readonly TreeProjectionCache treeProjections;
    private readonly SensorRegistry sensors;
    private readonly InputResolver inputResolver;
    private readonly ILogger<RepositorySnapshotPrewarmer> logger;

    public RepositorySnapshotPrewarmer(
        RepositoryRegistry registry,
        RepositoryHierarchyCache hierarchyCache,
        ProjectDashboardService dashboards,
        RepositorySnapshotStore snapshotStore,
        RepositorySensorAvailabilityCache sensorAvailabilityCache,
        TreeProjectionCache treeProjections,
        SensorRegistry sensors,
        InputResolver inputResolver,
        ILogger<RepositorySnapshotPrewarmer> logger)
    {
        this.registry = registry;
        this.hierarchyCache = hierarchyCache;
        this.dashboards = dashboards;
        this.snapshotStore = snapshotStore;
        this.sensorAvailabilityCache = sensorAvailabilityCache;
        this.treeProjections = treeProjections;
        this.sensors = sensors;
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
        var key = RepositoryCacheState.RegistrationFingerprint(registration);
        if (!pending.TryAdd(key, 0)) return;
        if (!queue.Writer.TryWrite(registration)) pending.TryRemove(key, out _);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registry.List())
        {
            var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
                ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
                : registration.GlobalInputsDirectory;
            await snapshotStore.TryRestoreAsync(registration, globalDirectory, cancellationToken);
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Snapshot verification is synchronous; cold snapshot construction remains in the background.
        await Task.Yield();
        QueueAll(registry.List());
        await foreach (var registration in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var key = RepositoryCacheState.RegistrationFingerprint(registration);
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
                var treeProjectionStarted = Stopwatch.GetTimestamp();
                var findingStates = await new FindingStateStore(registration.RootPath).ReadAsync(stoppingToken);
                var treeProjection = treeProjections.GetMeasured(
                    registration.RootPath,
                    hierarchy.Snapshot,
                    findingStates,
                    CoverageSnapshot.Load(registration.RootPath),
                    CoverageSensor.GitValue(registration.RootPath, "rev-parse", "--verify", "HEAD"));
                var treeProjectionMilliseconds = Stopwatch.GetElapsedTime(treeProjectionStarted).TotalMilliseconds;
                var sensorAvailability = await sensorAvailabilityCache.GetAsync(
                    registration, sensors, stoppingToken);
                if (!hierarchy.CacheHit || !projection.CacheHit || !sensorAvailability.CacheHit)
                {
                    await snapshotStore.SaveAsync(
                        registration,
                        hierarchy.Snapshot,
                        projection.Dashboard,
                        sensorAvailability.Availability,
                        stoppingToken);
                }
                var prewarmEvent = JsonSerializer.Serialize(new
                {
                    @event = "qs.repository.prewarm",
                    repositoryId = registration.Id,
                    cache = hierarchy.CacheHit && projection.CacheHit && treeProjection.CacheHit &&
                            sensorAvailability.CacheHit ? "warm" : "cold",
                    durationMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2),
                    phases = new
                    {
                        gitStatusMs = Math.Round(hierarchy.GitStatusMilliseconds, 2),
                        scanMs = Math.Round(hierarchy.ScanMilliseconds, 2),
                        reviewMetaDiscoveryMs = Math.Round(hierarchy.ReviewMetaDiscoveryMilliseconds, 2),
                        projectionMs = Math.Round(projection.ProjectionMilliseconds, 2),
                        treeProjectionMs = Math.Round(treeProjectionMilliseconds, 2),
                        sensorStateMs = Math.Round(sensorAvailability.RepositoryStateMilliseconds, 2),
                        sensorInitMs = Math.Round(sensorAvailability.InitializationMilliseconds, 2),
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
