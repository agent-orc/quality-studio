using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Disposition of a deterministic sensor (or the aggregate of several) once its findings are
/// filtered to a review subject. Ordered worst-to-best by underlying int value so aggregation can
/// take the max: an unavailable required check must never resolve to <see cref="Pass"/>, and a
/// compiler/build-severity finding always outranks an unavailable sibling sensor.
/// </summary>
public enum DeterministicDisposition
{
    Pass = 0,
    Warn = 1,
    Unavailable = 2,
    Block = 3,
}

public sealed class DeterministicEvidenceCollector(SensorRegistry registry)
{
    public async Task<IReadOnlyList<SensorScanResult>> CollectAsync(
        string repositoryRoot,
        IReadOnlyList<ReviewSensorConfiguration> configurations,
        CancellationToken cancellationToken = default)
    {
        var tasks = configurations
            .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
            .Select(configuration => CollectOneAsync(repositoryRoot, configuration, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(result => result is not null)
            .Select(result => result!)
            .OrderBy(result => result.Provenance.SensorId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<SensorScanResult?> CollectOneAsync(
        string repositoryRoot,
        ReviewSensorConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IReviewSensor sensor;
        try
        {
            sensor = registry.Get(configuration.Id);
        }
        catch (SensorNotFoundException)
        {
            return null;
        }
        if (sensor is not IDeterministicEvidenceSensor) return null;

        try
        {
            var result = await sensor.RunAsync(new SensorScanRequest(
                repositoryRoot,
                SensorScope.Repository,
                Configuration: configuration.Configuration,
                PersistMetadata: false), cancellationToken).ConfigureAwait(false);
            if (result.Findings.Any(finding =>
                    finding.Source?.Kind != FindingSourceKind.Deterministic ||
                    string.IsNullOrWhiteSpace(finding.Source.SensorId)))
            {
                throw new InvalidDataException(
                    $"Deterministic sensor '{sensor.Id}' returned a finding without deterministic source provenance.");
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SensorScanResult(
                false,
                $"Analyzer execution failed: {exception.Message}",
                [],
                new SensorProvenance(
                    sensor.Id,
                    sensor.Version,
                    "repository",
                    ".",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }
}

public static class DeterministicEvidenceProjection
{
    public static IReadOnlyList<SensorScanResult> ForSubjects(
        IReadOnlyList<SensorScanResult>? evidence,
        IReadOnlyList<string> subjectPaths)
    {
        if (evidence is not { Count: > 0 }) return [];
        var subjects = subjectPaths.Select(NormalizePath).ToHashSet(StringComparer.Ordinal);
        return evidence.Select(result => result with
            {
                Findings = result.Findings
                    .Where(finding => finding.Locations.Count == 0 ||
                                      finding.Locations.Any(location =>
                                          subjects.Contains(NormalizePath(location.Path))))
                    .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                    .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty,
                        StringComparer.Ordinal)
                    .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                    .ToArray(),
            })
            .OrderBy(result => result.Provenance.SensorId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Worst-of disposition across every sensor in <paramref name="evidence"/>. A compiler/build
    /// error (deterministic <see cref="FindingSeverity.Critical"/> or <see cref="FindingSeverity.High"/>)
    /// always yields <see cref="DeterministicDisposition.Block"/>; an unavailable sensor never
    /// resolves to <see cref="DeterministicDisposition.Pass"/> even when every other sensor is clean.
    /// </summary>
    public static DeterministicDisposition Classify(IReadOnlyList<SensorScanResult> evidence)
    {
        var worst = DeterministicDisposition.Pass;
        foreach (var result in evidence)
        {
            var disposition = ClassifySensor(result);
            if (disposition > worst) worst = disposition;
        }
        return worst;
    }

    public static DeterministicDisposition ClassifySensor(SensorScanResult result)
    {
        if (!result.Available) return DeterministicDisposition.Unavailable;
        if (result.Findings.Any(finding => finding.Severity is FindingSeverity.Critical or FindingSeverity.High))
            return DeterministicDisposition.Block;
        if (result.Findings.Any(finding => finding.Severity == FindingSeverity.Medium))
            return DeterministicDisposition.Warn;
        return DeterministicDisposition.Pass;
    }

    /// <summary>
    /// Compact prompt projection: rule id, severity, and location per finding plus per-sensor
    /// counts and disposition — never the finding's title, description, recommendation, or raw
    /// evidence text. The full findings remain available to humans/dashboards via the stored
    /// review metadata; only what reaches the model prompt is reduced.
    /// </summary>
    public static string ToPromptJson(IReadOnlyList<SensorScanResult> evidence)
    {
        var root = new JsonObject
        {
            ["disposition"] = DispositionName(Classify(evidence)),
            ["sensors"] = new JsonArray(evidence.Select(result => (JsonNode)SensorJson(result)).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string DispositionName(DeterministicDisposition disposition) =>
        disposition.ToString().ToLowerInvariant();

    private static JsonObject SensorJson(SensorScanResult result)
    {
        var severityCounts = Enum.GetValues<FindingSeverity>().ToDictionary(
            severity => severity.ToString().ToLowerInvariant(),
            severity => result.Findings.Count(finding => finding.Severity == severity));
        return new JsonObject
        {
            ["id"] = result.Provenance.SensorId,
            ["version"] = result.Provenance.SensorVersion,
            ["available"] = result.Available,
            ["unavailableReason"] = result.UnavailableReason,
            ["disposition"] = DispositionName(ClassifySensor(result)),
            ["findingCount"] = result.Findings.Count,
            ["severityCounts"] = new JsonObject(severityCounts
                .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value))),
            ["findings"] = new JsonArray(result.Findings.Select(finding => (JsonNode)new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
                ["path"] = finding.Locations.FirstOrDefault()?.Path,
                ["line"] = finding.Locations.FirstOrDefault()?.Range?.Start.Line,
            }).ToArray()),
        };
    }

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
