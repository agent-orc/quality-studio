namespace AgentOrchestrator.CodeQuality;

public enum SensorScope
{
    Repository,
    Path,
}

public sealed record SensorAvailability(
    bool Available,
    string? UnavailableReason = null,
    IReadOnlyDictionary<string, string>? ToolVersions = null);

public sealed record SensorScanRequest(
    string RepositoryRoot,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null,
    IReadOnlyDictionary<string, string>? Configuration = null,
    bool PersistMetadata = true);

public sealed record SensorProvenance(
    string SensorId,
    string SensorVersion,
    string Scope,
    string Target,
    string ScannedAt,
    IReadOnlyDictionary<string, string> ToolVersions);

public sealed record SensorScanResult(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance);

public interface IReviewSensor
{
    string Id { get; }

    string Version { get; }

    IReadOnlyList<SensorScope> SupportedScopes { get; }

    Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default);
}

public sealed class SensorRegistry
{
    private readonly IReadOnlyDictionary<string, IReviewSensor> sensors;

    public SensorRegistry(IEnumerable<IReviewSensor> sensors)
    {
        this.sensors = sensors.ToDictionary(sensor => sensor.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IReviewSensor> List() => sensors.Values
        .OrderBy(sensor => sensor.Id, StringComparer.Ordinal)
        .ToArray();

    public IReviewSensor Get(string id) =>
        sensors.TryGetValue(id, out var sensor)
            ? sensor
            : throw new SensorNotFoundException($"Sensor '{id}' was not found.");
}

public sealed class SensorNotFoundException(string message) : Exception(message);
