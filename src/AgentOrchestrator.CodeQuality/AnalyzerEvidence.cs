using System.Globalization;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

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

    public static string ToPromptJson(IReadOnlyList<SensorScanResult> evidence) =>
        JsonSerializer.Serialize(evidence, ReviewMetaJson.Options);

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
