using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        var projected = evidence
            .SelectMany(result => result.Findings.SelectMany(finding =>
                finding.Locations.DefaultIfEmpty(new FindingLocation("."))
                    .Select(location => new JsonObject
                    {
                        ["sensorId"] = result.Provenance.SensorId,
                        ["ruleId"] = finding.RuleId,
                        ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
                        ["path"] = NormalizePath(location.Path),
                        ["range"] = location.Range is null
                            ? null
                            : JsonSerializer.SerializeToNode(location.Range, ReviewMetaJson.Options),
                        ["resultHash"] = finding.Fingerprint,
                    })))
            .OrderBy(item => item["path"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(item => item["range"]?["start"]?["line"]?.GetValue<int>() ?? 0)
            .ThenBy(item => item["ruleId"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(item => item["sensorId"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToList();
        if (projected.Count == 0) return "[]";

        var omittedHashes = new List<string>();
        while (Serialize(projected).Length > MaximumPromptCharacters)
        {
            omittedHashes.Add(projected[^1]["resultHash"]!.GetValue<string>());
            projected.RemoveAt(projected.Count - 1);
        }

        if (omittedHashes.Count == 0) return Serialize(projected);
        while (true)
        {
            var withOverflow = projected
                .Select(item => item.DeepClone())
                .Append(new JsonObject
                {
                    ["omitted"] = omittedHashes.Count,
                    ["resultHash"] = Hash(omittedHashes),
                })
                .ToList();
            var json = Serialize(withOverflow);
            if (json.Length <= MaximumPromptCharacters) return json;
            if (projected.Count == 0)
                throw new InvalidOperationException("Deterministic evidence overflow marker exceeds prompt ceiling.");
            omittedHashes.Add(projected[^1]["resultHash"]!.GetValue<string>());
            projected.RemoveAt(projected.Count - 1);
        }
    }

    private static string Serialize(IEnumerable<JsonNode> nodes) =>
        new JsonArray(nodes.Select(node => node.DeepClone()).ToArray())
            .ToJsonString(ReviewMetaJson.Options);

    private static string Hash(IEnumerable<string> values) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\0', values.Order(StringComparer.Ordinal)))));

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
