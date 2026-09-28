using System.Text.Json.Serialization;

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

/// <param name="SuppressedFindings">
/// Results the producer reported as suppressed at source (SARIF <c>suppressions</c>, for example a
/// <c>#pragma warning disable</c> or an <c>eslint-disable</c> comment). They are not findings, but the
/// count stays visible so a clean result can be told apart from a silenced one.
/// </param>
public sealed record SensorScanResult(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings,
    SensorProvenance Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int SuppressedFindings = 0);

public sealed record ReviewSensorConfiguration(
    string Id,
    IReadOnlyDictionary<string, string>? Configuration = null);

public interface IReviewSensor
{
    string Id { get; }

    string Version { get; }

    IReadOnlyList<SensorScope> SupportedScopes { get; }

    Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Marks sensor output that is safe to expose to an agent as prior deterministic evidence.
/// The evidence remains separate from findings authored by the review agent.
/// </summary>
public interface IDeterministicEvidenceSensor : IReviewSensor;

/// <summary>
/// A sensor whose availability depends on the analysed repository: the SDK its <c>global.json</c> pins,
/// or the Node tools its workspace installed. The host-wide probe runs in the host's working
/// directory and would report the host's own tools instead.
/// </summary>
public interface IRepositoryProbedSensor : IReviewSensor
{
    Task<SensorAvailability> ProbeAvailabilityAsync(
        string repositoryRoot,
        IReadOnlyDictionary<string, string>? configuration,
        CancellationToken cancellationToken = default);
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
