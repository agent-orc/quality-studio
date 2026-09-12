using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Persists verified immutable hierarchy and dashboard projections under the API host.
/// Repository files are never used for cache storage.
/// </summary>
public sealed class RepositorySnapshotStore
{
    private const int SchemaVersion = 2;
    private readonly string snapshotsDirectory;
    private readonly RepositoryHierarchyCache hierarchyCache;
    private readonly ProjectDashboardService dashboards;
    private readonly RepositorySensorAvailabilityCache sensorAvailabilityCache;
    private readonly ILogger<RepositorySnapshotStore> logger;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public RepositorySnapshotStore(
        IHostEnvironment environment,
        RepositoryHierarchyCache hierarchyCache,
        ProjectDashboardService dashboards,
        RepositorySensorAvailabilityCache sensorAvailabilityCache,
        ILogger<RepositorySnapshotStore> logger)
    {
        snapshotsDirectory = Path.Combine(environment.ContentRootPath, ".quality-studio", "cache", "repositories");
        this.hierarchyCache = hierarchyCache;
        this.dashboards = dashboards;
        this.sensorAvailabilityCache = sensorAvailabilityCache;
        this.logger = logger;
    }

    public async Task<bool> TryRestoreAsync(
        RepositoryRegistration registration,
        string? globalInputsDirectory,
        CancellationToken cancellationToken)
    {
        var path = SnapshotPath(registration.Id);
        if (!File.Exists(path)) return false;

        try
        {
            var state = hierarchyCache.MeasureState(
                registration.RootPath,
                globalInputsDirectory,
                registration.InputBudgetCharacters);
            await using var stream = File.OpenRead(path);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedRepositorySnapshot>(
                stream, jsonOptions, cancellationToken);
            if (persisted is null ||
                persisted.SchemaVersion != SchemaVersion ||
                !StringComparer.OrdinalIgnoreCase.Equals(persisted.RepositoryId, registration.Id) ||
                !StringComparer.Ordinal.Equals(
                    persisted.RegistrationFingerprint,
                    RepositoryCacheState.RegistrationFingerprint(registration)) ||
                !StringComparer.Ordinal.Equals(persisted.HeadSha, state.HeadSha) ||
                !StringComparer.Ordinal.Equals(persisted.RepositoryState, state.State))
            {
                return false;
            }

            var snapshot = new RepositoryHierarchySnapshot(
                persisted.Roots.Select(RestoreNode).ToArray(),
                persisted.RepositoryState,
                persisted.ETag);
            hierarchyCache.Seed(registration.RootPath, snapshot);
            dashboards.Seed(registration.RootPath, snapshot, persisted.Dashboard);
            sensorAvailabilityCache.Seed(
                registration, persisted.RepositoryState, persisted.SensorAvailability);
            logger.LogInformation(new EventId(1114, "RepositorySnapshotRestored"),
                "Restored repository snapshot for {RepositoryId} at {HeadSha}",
                registration.Id, state.HeadSha);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                          InvalidDataException or ArgumentException or InvalidOperationException or
                                          NullReferenceException)
        {
            logger.LogWarning(new EventId(1115, "RepositorySnapshotRejected"), exception,
                "Ignored corrupt or unreadable repository snapshot for {RepositoryId}", registration.Id);
            return false;
        }
    }

    public async Task SaveAsync(
        RepositoryRegistration registration,
        RepositoryHierarchySnapshot snapshot,
        ProjectDashboardResponse dashboard,
        IReadOnlyDictionary<string, SensorAvailability> sensorAvailability,
        CancellationToken cancellationToken)
    {
        var globalDirectory = string.IsNullOrWhiteSpace(registration.GlobalInputsDirectory)
            ? Environment.GetEnvironmentVariable("QUALITY_GLOBAL_INPUTS")
            : registration.GlobalInputsDirectory;
        var state = hierarchyCache.MeasureState(
            registration.RootPath,
            globalDirectory,
            registration.InputBudgetCharacters);
        if (!StringComparer.Ordinal.Equals(state.State, snapshot.GitState)) return;

        Directory.CreateDirectory(snapshotsDirectory);
        var persisted = new PersistedRepositorySnapshot(
            SchemaVersion,
            registration.Id,
            RepositoryCacheState.RegistrationFingerprint(registration),
            state.HeadSha,
            state.State,
            snapshot.ETag,
            DateTimeOffset.UtcNow,
            snapshot.Roots.Select(PersistNode).ToArray(),
            dashboard,
            sensorAvailability);
        var path = SnapshotPath(registration.Id);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, true);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private string SnapshotPath(string repositoryId) => Path.Combine(snapshotsDirectory, $"{repositoryId}.json");

    private static PersistedHierarchyNode PersistNode(HierarchyNode node) => new(
        node.Id,
        node.Name,
        node.Level,
        node.Path,
        node.SizeBytes,
        node.LineCount,
        node.Exclusions.ToArray(),
        node.Documents.Values.Select(document => new PersistedReviewDocument(
            document.UnitId,
            document.Kind,
            document.State,
            document.SourcePath,
            document.Payload)).ToArray(),
        node.Children.Select(PersistNode).ToArray());

    private static HierarchyNode RestoreNode(PersistedHierarchyNode persisted)
    {
        var node = new HierarchyNode(
            persisted.Id,
            persisted.Name,
            persisted.Level,
            persisted.Path,
            persisted.SizeBytes,
            persisted.LineCount);
        node.AddExclusions(persisted.Exclusions);
        foreach (var document in persisted.Documents)
        {
            node.Attach(new AttachedReviewMetaDocument(
                document.UnitId,
                document.Kind,
                document.State,
                document.SourcePath,
                document.Payload));
        }
        foreach (var child in persisted.Children) node.AddChild(RestoreNode(child));
        return node;
    }

    private sealed record PersistedRepositorySnapshot(
        int SchemaVersion,
        string RepositoryId,
        string RegistrationFingerprint,
        string HeadSha,
        string RepositoryState,
        string ETag,
        DateTimeOffset CreatedAt,
        IReadOnlyList<PersistedHierarchyNode> Roots,
        ProjectDashboardResponse Dashboard,
        IReadOnlyDictionary<string, SensorAvailability> SensorAvailability);

    private sealed record PersistedHierarchyNode(
        string Id,
        string Name,
        ReviewLevel Level,
        string Path,
        long? SizeBytes,
        int? LineCount,
        IReadOnlyList<ScopeExclusion> Exclusions,
        IReadOnlyList<PersistedReviewDocument> Documents,
        IReadOnlyList<PersistedHierarchyNode> Children);

    private sealed record PersistedReviewDocument(
        string UnitId,
        ReviewKind Kind,
        ReviewState State,
        string SourcePath,
        string? Payload);
}
