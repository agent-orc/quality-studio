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
    public const int MaximumPromptCharacters = 2000;

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
        var projection = new JsonArray();
        foreach (var result in evidence
                     .Where(candidate => candidate.Findings.Count > 0)
                     .OrderBy(candidate => candidate.Provenance.SensorId, StringComparer.Ordinal))
        {
            var findings = new JsonArray();
            var projectedResult = new JsonObject
            {
                ["sensorId"] = result.Provenance.SensorId,
                ["resultHash"] = ResultHash(result),
                ["findings"] = findings,
            };
            projection.Add(projectedResult);
            var ordered = result.Findings
                .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty,
                    StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray();
            var omitted = 0;
            foreach (var finding in ordered)
            {
                var location = finding.Locations.FirstOrDefault();
                findings.Add(new JsonObject
                {
                    ["ruleId"] = finding.RuleId,
                    ["severity"] = finding.Severity.ToString().ToLowerInvariant(),
                    ["path"] = location?.Path,
                    ["range"] = location?.Range is null
                        ? null
                        : JsonSerializer.SerializeToNode(location.Range, ReviewMetaJson.Options),
                });
                if (PromptJson(projection).Length <= MaximumPromptCharacters) continue;
                findings.RemoveAt(findings.Count - 1);
                omitted++;
            }
            if (omitted > 0)
            {
                projectedResult["omittedFindings"] = omitted;
                while (PromptJson(projection).Length > MaximumPromptCharacters && findings.Count > 0)
                {
                    findings.RemoveAt(findings.Count - 1);
                    omitted++;
                    projectedResult["omittedFindings"] = omitted;
                }
            }
            if (PromptJson(projection).Length <= MaximumPromptCharacters) continue;

            projection.RemoveAt(projection.Count - 1);
            break;
        }

        return PromptJson(projection);
    }

    private static string ResultHash(SensorScanResult result)
    {
        var canonical = string.Join('\0', new[]
        {
            result.Provenance.SensorId,
            result.Provenance.SensorVersion,
        }.Concat(result.Findings
            .Select(finding => finding.Fingerprint)
            .OrderBy(fingerprint => fingerprint, StringComparer.Ordinal)));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string PromptJson(JsonArray projection) =>
        projection.ToJsonString(ReviewMetaJson.Options);

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
