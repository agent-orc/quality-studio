using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    public const int MaximumPromptCharacters = 2_000;

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

    public static string ToPromptJson(IReadOnlyList<SensorScanResult> evidence)
    {
        var projections = evidence
            .Where(result => result.Available && result.Findings.Count > 0)
            .OrderBy(result => result.Provenance.SensorId, StringComparer.Ordinal)
            .Select(result => new PromptSensorEvidence(
                result.Provenance.SensorId,
                result.Findings.Select(finding => finding.Source?.Producer)
                    .FirstOrDefault(producer => !string.IsNullOrWhiteSpace(producer)),
                ResultHash(result),
                result.Findings
                    .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty,
                        StringComparer.Ordinal)
                    .ThenBy(finding => finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
                    .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                    .Select(ToPromptFinding)
                    .ToList(),
                0))
            .ToList();
        if (projections.Count == 0) return "[]";

        var json = Serialize(projections);
        while (json.Length > MaximumPromptCharacters &&
               projections.Any(projection => projection.Findings.Count > 0))
        {
            var projection = projections.Last(candidate => candidate.Findings.Count > 0);
            projection.Findings.RemoveAt(projection.Findings.Count - 1);
            projection.OmittedFindings++;
            json = Serialize(projections);
        }

        if (json.Length <= MaximumPromptCharacters) return json;

        var aggregateHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', projections.Select(item => item.ResultHash)))));
        return Serialize(new[]
        {
            new PromptSensorEvidence(
                "deterministic-evidence", null, aggregateHash, [],
                evidence.Sum(result => result.Findings.Count)),
        });
    }

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    private static PromptFinding ToPromptFinding(ReviewFinding finding)
    {
        var location = finding.Locations.FirstOrDefault();
        return new PromptFinding(
            finding.RuleId,
            finding.Severity.ToString().ToLowerInvariant(),
            location is null ? null : NormalizePath(location.Path),
            location?.Range);
    }

    private static string ResultHash(SensorScanResult result)
    {
        var canonical = new StringBuilder()
            .Append(result.Provenance.SensorId).Append('\0')
            .Append(result.Provenance.SensorVersion).Append('\0')
            .Append(result.Available).Append('\0')
            .Append(result.UnavailableReason);
        foreach (var version in result.Provenance.ToolVersions.OrderBy(item => item.Key, StringComparer.Ordinal))
            canonical.Append('\0').Append(version.Key).Append('=').Append(version.Value);
        foreach (var finding in result.Findings.OrderBy(item => item.Fingerprint, StringComparer.Ordinal))
            canonical.Append('\0').Append(finding.Fingerprint);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, PromptJsonOptions);

    private static JsonSerializerOptions PromptJsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record PromptFinding(
        string RuleId,
        string Severity,
        string? Path,
        FindingRange? Range);

    private sealed class PromptSensorEvidence(
        string sensorId,
        string? producer,
        string resultHash,
        List<PromptFinding> findings,
        int omittedFindings)
    {
        public string SensorId { get; } = sensorId;
        public string? Producer { get; } = producer;
        public string ResultHash { get; } = resultHash;
        public List<PromptFinding> Findings { get; } = findings;
        public int OmittedFindings { get; set; } = omittedFindings;
    }
}
