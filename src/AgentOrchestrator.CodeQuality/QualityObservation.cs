using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationDocument
{
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;
    [JsonPropertyOrder(1)] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyOrder(2)] public required string ObservationId { get; init; }
    [JsonPropertyOrder(3)] public required DateTimeOffset ObservedAt { get; init; }
    [JsonPropertyOrder(4)] public required QualityTaxonomyReference Taxonomy { get; init; }
    [JsonPropertyOrder(5)] public required QualityObservationSubject Subject { get; init; }
    [JsonPropertyOrder(6)] public required QualityObservationProfile Profile { get; init; }
    [JsonPropertyOrder(7)] public required QualityObservationProducer Producer { get; init; }
    [JsonPropertyOrder(8)] public required string EvidenceStatus { get; init; }
    [JsonPropertyOrder(9)] public IReadOnlyList<QualityEvidence> Evidence { get; init; } = [];
    [JsonPropertyOrder(10)] public IReadOnlyList<QualityObservationAspect> Aspects { get; init; } = [];
    [JsonPropertyOrder(11)] public required string Assessment { get; init; }
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Decision { get; init; }
    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PolicyRef { get; init; }
    [JsonPropertyOrder(14)] public IReadOnlyList<QualityObservationFinding> Findings { get; init; } = [];
    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QualityObservationLegacy? Legacy { get; init; }
    [JsonPropertyOrder(99)] public Dictionary<string, JsonElement> Extensions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

public sealed record QualityTaxonomyReference
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public IReadOnlyList<QualityExtensionTaxonomyReference> ExtensionCatalogues { get; init; } = [];
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityExtensionTaxonomyReference
{
    public required string Id { get; init; }
    public required string Prefix { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationSubject
{
    public required string UnitId { get; init; }
    public required string ManifestHash { get; init; }
    public required string Path { get; init; }
    public required string Scope { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationProfile
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string PromptHash { get; init; }
    public required string ReviewInputsHash { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationProducer
{
    public required string Kind { get; init; }
    public required string Agent { get; init; }
    public required string Provider { get; init; }
    public required string RequestedModel { get; init; }
    public required string EffectiveModel { get; init; }
    public required string ThinkingLevel { get; init; }
    public required string RoutePolicyVersion { get; init; }
    public required string RunId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ReviewRunId { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityEvidence
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QualityEvidenceLocator? Locator { get; init; }
    public required string Summary { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContentHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MediaType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Content { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityEvidenceLocator
{
    public required string Path { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SymbolId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? StartLine { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? StartColumn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? EndLine { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? EndColumn { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationAspect
{
    public required string AspectId { get; init; }
    public string Axis { get; init; } = "assessment";
    public required string Assessment { get; init; }
    public required string Rationale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QualityObservationGrade? Grade { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationGrade
{
    public required int Score { get; init; }
    public required string Band { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationFinding
{
    public required string ObservationFindingId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IssueId { get; init; }
    public required string OccurrenceFingerprint { get; init; }
    public required string FingerprintAlgorithm { get; init; }
    public required string RuleRef { get; init; }
    public required string AspectId { get; init; }
    public required string Severity { get; init; }
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
    public required QualityFindingSource Source { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityFindingSource
{
    public required string Kind { get; init; }
    public required string ProducerRef { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SensorId { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationLegacy
{
    public required string Schema { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Value { get; init; }
    public required string SourcePath { get; init; }
    public required string Completeness { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public enum QualityObservationSupport { Supported, UnsupportedMajor }

public sealed record QualityObservationReadResult(
    QualityObservationSupport Support,
    QualityObservationDocument? Observation,
    JsonElement Raw);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Serialize(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationReadResult Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("A quality observation must be a JSON object.");
        var schemaVersion = raw.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var number)
            ? number
            : 0;
        var schema = raw.TryGetProperty("$schema", out var schemaNode) ? schemaNode.GetString() : null;
        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
        {
            return new QualityObservationReadResult(QualityObservationSupport.UnsupportedMajor, null, raw);
        }

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(raw.GetRawText(), Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        Validate(observation);
        return new QualityObservationReadResult(QualityObservationSupport.Supported, observation, raw);
    }

    private static void Validate(QualityObservationDocument observation)
    {
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(observation.ObservationId);
        if ((observation.Decision is null) != (observation.PolicyRef is null))
            throw new JsonException("An observation decision and policyRef must be supplied together.");
    }
}

public static class QualityObservationIdentity
{
    public static string Create(string runId, string unitId, string kind, string subjectHash,
        string reviewInputsHash, string taxonomyDigest)
    {
        var canonical = string.Join('\n', runId, unitId, kind, subjectHash, reviewInputsHash, taxonomyDigest);
        return "observation-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Append-only monthly observation ledger with idempotent replay and corrupt-line tolerance.</summary>
public static class QualityObservationLedger
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions LineOptions = new(QualityObservationJson.Options)
    {
        WriteIndented = false,
    };

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<bool> AppendAsync(string repositoryRoot, QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        QualityObservationJson.Serialize(observation); // Validate before touching the ledger.
        var line = JsonSerializer.Serialize(observation, LineOptions);
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                await foreach (var existing in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(existing)) continue;
                    try
                    {
                        var read = QualityObservationJson.Deserialize(existing);
                        if (read.Observation?.ObservationId == observation.ObservationId) return false;
                    }
                    catch (Exception exception) when (exception is JsonException or ArgumentException)
                    {
                        // A malformed historical line must not hide later valid observations.
                    }
                }
            }

            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n') await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationDocument>> ReadAsync(
        string repositoryRoot, CancellationToken cancellationToken = default)
    {
        var observations = new List<QualityObservationDocument>();
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return observations;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl").Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var read = QualityObservationJson.Deserialize(line);
                    if (read.Observation is not null) observations.Add(read.Observation);
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    // Keep reading after corrupt or partial lines.
                }
            }
        }
        return observations;
    }
}

internal static class ReviewObservationProjector
{
    public static QualityObservationDocument Create(
        JsonObject response,
        string observationId,
        DateTimeOffset observedAt,
        string unitId,
        string path,
        ReviewLevel level,
        string kind,
        string subjectHash,
        string promptHash,
        string reviewInputsHash,
        bool completeInputs,
        string sidecarPath,
        ReviewUsageEntry usage)
    {
        var evidence = new List<QualityEvidence>();
        var findings = new List<QualityObservationFinding>();
        var findingIndex = 0;
        foreach (var finding in response["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            findingIndex++;
            var refs = new List<string>();
            var locationIndex = 0;
            foreach (var location in finding["locations"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                locationIndex++;
                var evidenceId = $"ev-{findingIndex}-source-{locationIndex}";
                var range = location["range"] as JsonObject;
                evidence.Add(new QualityEvidence
                {
                    Id = evidenceId,
                    Kind = "source-code",
                    Summary = finding["title"]?.GetValue<string>() ?? "Finding location.",
                    Locator = new QualityEvidenceLocator
                    {
                        Path = location["path"]?.GetValue<string>() ?? path,
                        SymbolId = location["symbolId"]?.GetValue<string>(),
                        StartLine = range?["start"]?["line"]?.GetValue<int>(),
                        StartColumn = range?["start"]?["column"]?.GetValue<int>(),
                        EndLine = range?["end"]?["line"]?.GetValue<int>(),
                        EndColumn = range?["end"]?["column"]?.GetValue<int>(),
                    },
                });
                refs.Add(evidenceId);
            }

            if (finding["evidence"] is JsonNode legacyEvidence)
            {
                var evidenceId = $"ev-{findingIndex}-fact";
                var value = legacyEvidence is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
                    ? text
                    : legacyEvidence.ToJsonString();
                evidence.Add(QualityLegacyMapping.Evidence(evidenceId, value));
                refs.Add(evidenceId);
            }

            var source = Source(finding);
            var legacyAspect = finding["aspect"]?.GetValue<string>() ?? "unknown";
            findings.Add(new QualityObservationFinding
            {
                ObservationFindingId = finding["id"]?.GetValue<string>() ?? $"of-{findingIndex}",
                OccurrenceFingerprint = finding["fingerprint"]?.GetValue<string>() ?? "unknown",
                FingerprintAlgorithm = "quality-studio-occurrence-v1",
                RuleRef = finding["ruleId"]?.GetValue<string>() ?? "unknown",
                AspectId = QualityLegacyMapping.Aspect(legacyAspect),
                Severity = finding["severity"]?.GetValue<string>() ?? "info",
                EvidenceRefs = refs,
                Source = source,
                Extensions = LegacyValue(legacyAspect),
            });
        }

        var aspects = (response["aspects"]?.AsArray().OfType<JsonObject>() ?? []).Select(aspect =>
        {
            var legacyId = aspect["id"]?.GetValue<string>() ?? "unknown";
            var grade = aspect["grade"] as JsonObject;
            return new QualityObservationAspect
            {
                AspectId = QualityLegacyMapping.Aspect(legacyId),
                Assessment = QualityTaxonomyTerms.AssessmentNotAssessed,
                Rationale = grade?["rationale"]?.GetValue<string>() ?? string.Empty,
                Grade = grade is null ? null : new QualityObservationGrade
                {
                    Score = grade["score"]?.GetValue<int>() ?? 0,
                    Band = grade["band"]?.GetValue<string>() ?? "F",
                },
                Extensions = LegacyValue(legacyId),
            };
        }).ToArray();

        var assessment = QualityTaxonomyTerms.AssessmentNotAssessed;
        string? decision = null;
        string? policyRef = null;
        var evidenceStatus = completeInputs ? QualityTaxonomyTerms.EvidenceAvailable : QualityTaxonomyTerms.EvidencePartial;
        if (response["securityVerdict"]?.GetValue<string>() is { } responseVerdict)
        {
            var mapped = QualityLegacyMapping.SecurityVerdict(responseVerdict);
            assessment = mapped.Assessment!;
            decision = mapped.Decision;
            policyRef = mapped.PolicyRef;
            evidenceStatus = mapped.EvidenceStatus!;
        }

        return new QualityObservationDocument
        {
            ObservationId = observationId,
            ObservedAt = observedAt,
            Taxonomy = new QualityTaxonomyReference
            {
                Id = QualityTaxonomyTerms.CatalogueId,
                Version = QualityTaxonomyTerms.CatalogueVersion,
                Digest = QualityTaxonomyCatalogue.Digest,
            },
            Subject = new QualityObservationSubject
            {
                UnitId = unitId,
                ManifestHash = Hash(subjectHash),
                Path = path,
                Scope = level.ToString().ToLowerInvariant(),
            },
            Profile = new QualityObservationProfile
            {
                Id = $"{level.ToString().ToLowerInvariant()}-{kind}-review",
                Version = "1.0.0",
                PromptHash = Hash(promptHash),
                ReviewInputsHash = Hash(reviewInputsHash),
            },
            Producer = new QualityObservationProducer
            {
                Kind = QualityTaxonomyTerms.ProducerAgent,
                Agent = usage.CliType,
                Provider = usage.Provider ?? "unknown",
                RequestedModel = usage.RequestedModel ?? "unknown",
                EffectiveModel = usage.EffectiveModel ?? "unknown",
                ThinkingLevel = usage.ThinkingLevel ?? "unknown",
                RoutePolicyVersion = usage.RoutePolicyVersion ?? "unknown",
                RunId = usage.RunId,
                ReviewRunId = usage.ReviewRunId,
            },
            EvidenceStatus = evidenceStatus,
            Evidence = evidence,
            Aspects = aspects,
            Assessment = assessment,
            Decision = decision,
            PolicyRef = policyRef,
            Findings = findings,
            Legacy = new QualityObservationLegacy
            {
                Schema = ReviewMetaDocument.SchemaId,
                SourcePath = sidecarPath,
                Completeness = "complete",
            },
        };
    }

    private static QualityFindingSource Source(JsonObject finding)
    {
        var source = finding["source"] as JsonObject;
        if (string.Equals(source?["kind"]?.GetValue<string>(), "deterministic", StringComparison.Ordinal))
        {
            return new QualityFindingSource
            {
                Kind = QualityTaxonomyTerms.ProducerDeterministicSensor,
                ProducerRef = "evidence",
                SensorId = source?["sensorId"]?.GetValue<string>(),
            };
        }
        if (finding["evidence"] is JsonValue value && value.TryGetValue<string>(out var evidenceText))
        {
            try
            {
                using var parsed = JsonDocument.Parse(evidenceText);
                var root = parsed.RootElement;
                if (root.TryGetProperty("source", out var kind) && kind.GetString() == "machine-sensor")
                {
                    return new QualityFindingSource
                    {
                        Kind = QualityTaxonomyTerms.ProducerDeterministicSensor,
                        ProducerRef = "evidence",
                        SensorId = root.TryGetProperty("sensorId", out var sensor) ? sensor.GetString() : null,
                    };
                }
            }
            catch (JsonException)
            {
                // Plain legacy evidence does not establish deterministic provenance.
            }
        }
        return new QualityFindingSource { Kind = QualityTaxonomyTerms.ProducerAgent, ProducerRef = "self" };
    }

    private static Dictionary<string, JsonElement> LegacyValue(string value) => new()
    {
        ["legacyValue"] = JsonSerializer.SerializeToElement(value),
    };

    private static string Hash(string value)
    {
        if (value.StartsWith("sha256:", StringComparison.Ordinal) && value.Length == 71) return value;
        if (value.Length == 64 && value.All(Uri.IsHexDigit)) return "sha256:" + value.ToLowerInvariant();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
