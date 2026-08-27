using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public enum PreflightStatus
{
    Pass,
    Findings,
    Blocked,
    Unavailable,
    Failed,
}

public sealed record PreflightSubject(
    string ManifestHash,
    string? Commit,
    IReadOnlyList<string> Paths)
{
    public static PreflightSubject Create(
        IEnumerable<KeyValuePair<string, string>> subjectHashes,
        string? commit = null)
    {
        ArgumentNullException.ThrowIfNull(subjectHashes);
        var ordered = subjectHashes
            .Select(pair => KeyValuePair.Create(
                DeterministicEvidenceProjection.NormalizePath(pair.Key), pair.Value))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0) throw new ArgumentException("A preflight subject requires at least one path.", nameof(subjectHashes));
        var canonical = string.Join('\n', ordered.Select(pair => $"{pair.Key}\0{pair.Value}"));
        var manifestHash = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new PreflightSubject(manifestHash, commit, ordered.Select(pair => pair.Key).ToArray());
    }
}

public sealed record PreflightCheck(
    string Id,
    string Version,
    bool Required,
    IReadOnlyDictionary<string, string> ToolVersions,
    string ConfigurationHash,
    string CommandId,
    PreflightGateDisposition GateDisposition = PreflightGateDisposition.Continue);

/// <summary>
/// Immutable v1 normalization of an existing sensor result for one exact repository snapshot.
/// Timestamps and duration are retained as operational evidence but excluded from <see cref="ResultHash"/>.
/// </summary>
public sealed record PreflightResult(
    int SchemaVersion,
    PreflightSubject Subject,
    PreflightCheck Check,
    PreflightStatus Status,
    long DurationMs,
    string ResultHash,
    IReadOnlyList<ReviewFinding> Findings,
    string? StatusReason,
    SensorProvenance Provenance)
{
    public const int CurrentSchemaVersion = 1;

    public SensorScanResult ToSensorResult() => new(
        Status is not (PreflightStatus.Unavailable or PreflightStatus.Failed),
        StatusReason,
        Findings,
        Provenance);
}

public sealed record PreflightSnapshot(
    int SchemaVersion,
    string RunId,
    PreflightSubject Subject,
    string ConfigurationHash,
    string ResultHash,
    IReadOnlyList<PreflightResult> Results,
    long DurationMs = 0)
{
    public const int CurrentSchemaVersion = 1;

    public bool BlocksModel => Results.Any(result =>
        result.Check.Required && result.Status is PreflightStatus.Unavailable or PreflightStatus.Failed or PreflightStatus.Blocked);

    public IReadOnlyList<SensorScanResult> SensorResults() => Results
        .Select(result => result.ToSensorResult())
        .ToArray();
}

public sealed class PreflightCollector(SensorRegistry registry)
{
    public async Task<PreflightSnapshot> CollectAsync(
        string runId,
        string repositoryRoot,
        PreflightSubject subject,
        IReadOnlyList<ReviewSensorConfiguration> configurations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(configurations);

        var stopwatch = Stopwatch.StartNew();
        var normalizedConfigurations = configurations
            .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(configuration => configuration.Id, StringComparer.Ordinal)
            .ToArray();
        var configurationHash = await ConfigurationSetHashAsync(normalizedConfigurations, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<PreflightResult>(normalizedConfigurations.Length);
        foreach (var configuration in normalizedConfigurations.Where(configuration =>
                     IsPrerequisite(configuration.Id)))
        {
            results.Add(await CollectOneAsync(repositoryRoot, subject, configuration, cancellationToken)
                .ConfigureAwait(false));
        }
        var tasks = normalizedConfigurations
            .Where(configuration => !IsPrerequisite(configuration.Id))
            .Select(configuration => CollectOneAsync(repositoryRoot, subject, configuration, cancellationToken));
        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        var ordered = results.OrderBy(result => result.Check.Id, StringComparer.Ordinal).ToArray();
        return new PreflightSnapshot(
            PreflightSnapshot.CurrentSchemaVersion,
            runId,
            subject,
            configurationHash,
            HashSnapshot(subject, configurationHash, ordered),
            ordered,
            stopwatch.ElapsedMilliseconds);
    }

    public async Task<string> ConfigurationSetHashAsync(
        IReadOnlyList<ReviewSensorConfiguration> configurations,
        CancellationToken cancellationToken = default)
    {
        var values = new List<JsonNode>();
        foreach (var configuration in configurations
                     .DistinctBy(configuration => configuration.Id, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(configuration => configuration.Id, StringComparer.Ordinal))
        {
            var available = false;
            IReadOnlyDictionary<string, string> toolVersions = new Dictionary<string, string>();
            try
            {
                var availability = await registry.Get(configuration.Id)
                    .ProbeAvailabilityAsync(cancellationToken).ConfigureAwait(false);
                available = availability.Available;
                toolVersions = availability.ToolVersions ?? new Dictionary<string, string>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is SensorNotFoundException or IOException or InvalidOperationException)
            {
                // The unavailable state is part of the cache identity. The bounded reason remains
                // in the normalized check result and is deliberately not hashed here.
            }

            values.Add(new JsonObject
            {
                ["id"] = configuration.Id,
                ["sensorVersion"] = TryVersion(registry, configuration.Id),
                ["required"] = configuration.Required,
                ["commandId"] = configuration.CommandId ?? configuration.Id,
                ["gateDisposition"] = JsonNamingPolicy.CamelCase.ConvertName(
                    TryGateDisposition(registry, configuration.Id).ToString()),
                ["configuration"] = DictionaryJson(configuration.Configuration),
                ["available"] = available,
                ["toolVersions"] = DictionaryJson(toolVersions),
            });
        }
        return Hash(new JsonArray(values.ToArray()).ToJsonString());
    }

    private async Task<PreflightResult> CollectOneAsync(
        string repositoryRoot,
        PreflightSubject subject,
        ReviewSensorConfiguration configuration,
        CancellationToken cancellationToken)
    {
        IReviewSensor sensor;
        try
        {
            sensor = registry.Get(configuration.Id);
        }
        catch (SensorNotFoundException exception)
        {
            return Failed(subject, configuration, "unknown", exception.Message, PreflightStatus.Unavailable, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await sensor.RunAsync(new SensorScanRequest(
                repositoryRoot,
                SensorScope.Repository,
                Configuration: configuration.Configuration,
                PersistMetadata: false), cancellationToken).ConfigureAwait(false);
            ValidateDeterministicFindings(sensor, result);
            var status = !result.Available ||
                         (result.Findings.Count == 0 && !string.IsNullOrWhiteSpace(result.UnavailableReason))
                ? PreflightStatus.Unavailable
                : sensor is ISelectivePreflightGateSensor gate && gate.HasBlockingFindings(result)
                    ? PreflightStatus.Blocked
                : result.Findings.Count > 0
                    ? PreflightStatus.Findings
                    : PreflightStatus.Pass;
            return Normalize(subject, configuration, sensor.Version, result, status, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(subject, configuration, sensor.Version,
                $"Sensor execution failed: {exception.Message}", PreflightStatus.Failed, stopwatch.ElapsedMilliseconds);
        }
    }

    private PreflightResult Normalize(
        PreflightSubject subject,
        ReviewSensorConfiguration configuration,
        string sensorVersion,
        SensorScanResult result,
        PreflightStatus status,
        long durationMs)
    {
        var check = new PreflightCheck(
            result.Provenance.SensorId,
            sensorVersion,
            configuration.Required,
            result.Provenance.ToolVersions,
            CheckConfigurationHash(
                configuration,
                sensorVersion,
                result.Provenance.ToolVersions,
                GateDisposition(configuration.Id)),
            configuration.CommandId ?? configuration.Id,
            GateDisposition(configuration.Id));
        var orderedFindings = result.Findings
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var draft = new PreflightResult(
            PreflightResult.CurrentSchemaVersion,
            subject,
            check,
            status,
            durationMs,
            string.Empty,
            orderedFindings,
            result.UnavailableReason,
            result.Provenance);
        return draft with { ResultHash = HashResult(draft) };
    }

    private PreflightResult Failed(
        PreflightSubject subject,
        ReviewSensorConfiguration configuration,
        string sensorVersion,
        string reason,
        PreflightStatus status,
        long durationMs)
    {
        var provenance = new SensorProvenance(
            configuration.Id,
            sensorVersion,
            "repository",
            ".",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            new Dictionary<string, string>(StringComparer.Ordinal));
        return Normalize(subject, configuration, sensorVersion,
            new SensorScanResult(false, reason, [], provenance), status, durationMs);
    }

    private static void ValidateDeterministicFindings(IReviewSensor sensor, SensorScanResult result)
    {
        if (sensor is not IDeterministicEvidenceSensor) return;
        if (result.Findings.Any(finding =>
                finding.Source?.Kind != FindingSourceKind.Deterministic ||
                string.IsNullOrWhiteSpace(finding.Source.SensorId)))
        {
            throw new InvalidDataException(
                $"Deterministic sensor '{sensor.Id}' returned a finding without deterministic source provenance.");
        }
    }

    private static string CheckConfigurationHash(
        ReviewSensorConfiguration configuration,
        string sensorVersion,
        IReadOnlyDictionary<string, string> toolVersions,
        PreflightGateDisposition gateDisposition) => Hash(new JsonObject
        {
            ["id"] = configuration.Id,
            ["sensorVersion"] = sensorVersion,
            ["required"] = configuration.Required,
            ["commandId"] = configuration.CommandId ?? configuration.Id,
            ["gateDisposition"] = JsonNamingPolicy.CamelCase.ConvertName(gateDisposition.ToString()),
            ["configuration"] = DictionaryJson(configuration.Configuration),
            ["toolVersions"] = DictionaryJson(toolVersions),
        }.ToJsonString());

    private static string HashResult(PreflightResult result) => Hash(new JsonObject
    {
        ["schemaVersion"] = result.SchemaVersion,
        ["subject"] = SubjectJson(result.Subject),
        ["check"] = CheckJson(result.Check),
        ["status"] = JsonNamingPolicy.CamelCase.ConvertName(result.Status.ToString()),
        ["statusReason"] = result.StatusReason,
        ["findings"] = JsonSerializer.SerializeToNode(result.Findings, ReviewMetaJson.Options),
    }.ToJsonString());

    private static string HashSnapshot(
        PreflightSubject subject,
        string configurationHash,
        IReadOnlyList<PreflightResult> results) => Hash(new JsonObject
        {
            ["schemaVersion"] = PreflightSnapshot.CurrentSchemaVersion,
            ["subject"] = SubjectJson(subject),
            ["configurationHash"] = configurationHash,
            ["results"] = new JsonArray(results.Select(result => (JsonNode)new JsonObject
            {
                ["id"] = result.Check.Id,
                ["resultHash"] = result.ResultHash,
            }).ToArray()),
        }.ToJsonString());

    private static JsonObject SubjectJson(PreflightSubject subject) => new()
    {
        ["manifestHash"] = subject.ManifestHash,
        ["commit"] = subject.Commit,
        ["paths"] = new JsonArray(subject.Paths.Order(StringComparer.Ordinal).Select(path => (JsonNode)path).ToArray()),
    };

    private static JsonObject CheckJson(PreflightCheck check) => new()
    {
        ["id"] = check.Id,
        ["version"] = check.Version,
        ["required"] = check.Required,
        ["toolVersions"] = DictionaryJson(check.ToolVersions),
        ["configurationHash"] = check.ConfigurationHash,
        ["commandId"] = check.CommandId,
        ["gateDisposition"] = JsonNamingPolicy.CamelCase.ConvertName(check.GateDisposition.ToString()),
    };

    private static JsonObject DictionaryJson(IReadOnlyDictionary<string, string>? values) => new(
        (values ?? new Dictionary<string, string>())
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)));

    private static string TryVersion(SensorRegistry registry, string id)
    {
        try
        {
            return registry.Get(id).Version;
        }
        catch (SensorNotFoundException)
        {
            return "unknown";
        }
    }

    private static PreflightGateDisposition TryGateDisposition(SensorRegistry registry, string id)
    {
        try
        {
            return registry.Get(id) is ISelectivePreflightGateSensor sensor
                ? sensor.GateDisposition
                : PreflightGateDisposition.Continue;
        }
        catch (SensorNotFoundException)
        {
            return PreflightGateDisposition.Continue;
        }
    }

    private PreflightGateDisposition GateDisposition(string id) => TryGateDisposition(registry, id);

    private bool IsPrerequisite(string id)
    {
        try
        {
            return registry.Get(id) is IPreflightPrerequisiteSensor;
        }
        catch (SensorNotFoundException)
        {
            return false;
        }
    }

    private static string Hash(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}

public static class PreflightProjection
{
    public static IReadOnlyList<PreflightResult> ForSubjects(
        IReadOnlyList<PreflightResult>? results,
        IReadOnlyList<string> subjectPaths)
    {
        if (results is not { Count: > 0 }) return [];
        var subjects = subjectPaths.Select(DeterministicEvidenceProjection.NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        return results.Select(result => result with
        {
            Findings = result.Findings
                    .Where(finding => finding.Locations.Count == 0 || finding.Locations.Any(location =>
                        subjects.Contains(DeterministicEvidenceProjection.NormalizePath(location.Path))))
                    .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                    .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                    .ToArray(),
        })
            .OrderBy(result => result.Check.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
