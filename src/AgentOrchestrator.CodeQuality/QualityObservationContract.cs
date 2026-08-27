using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum ObservationProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }

public enum ObservationEvidenceStatus { Available, Partial, Unavailable }

public enum ObservationAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }

public enum ObservationEvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

public sealed record QualityObservationSubject(string UnitId, string ManifestHash);

public sealed record QualityObservationProfile(string Id, string Version, string PromptHash, string ReviewInputsHash);

public sealed record QualityObservationProducer(
    ObservationProducerKind Kind,
    string? Agent = null,
    string? Provider = null,
    string? RequestedModel = null,
    string? EffectiveModel = null,
    string? ThinkingLevel = null,
    string? RoutePolicyVersion = null,
    string? RunId = null,
    string? ReviewRunId = null);

public sealed record QualityObservationEvidence(
    string Id,
    ObservationEvidenceKind Kind,
    IReadOnlyDictionary<string, JsonElement> Locator,
    string Summary,
    string? ContentHash = null);

public sealed record QualityObservationGrade(int Score, GradeBand Band);

public sealed record QualityObservationAspectAssessment(
    string AspectId,
    ObservationAssessment Assessment,
    string? Rationale = null,
    QualityObservationGrade? Grade = null);

public sealed record QualityObservationFindingSource(ObservationProducerKind Kind, string? ProducerRef = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string AspectId,
    FindingSeverity Severity,
    string? IssueId = null,
    string? OccurrenceFingerprint = null,
    string? FingerprintAlgorithm = null,
    string? RuleRef = null,
    IReadOnlyList<string>? EvidenceRefs = null,
    QualityObservationFindingSource? Source = null);

/// <summary>One producer's immutable result for one exact subject and input set (dossier section 7).
/// Nothing in T1 writes this envelope yet; <c>ReviewRunner</c> and other writers are unchanged.</summary>
public sealed record QualityObservationEnvelope
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
    public const string ObservationIdPrefix = "observation-sha256:";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string ObservationId { get; init; }

    [JsonPropertyOrder(3)]
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required ObservationEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<QualityObservationEvidence> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityObservationAspectAssessment> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required ObservationAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    /// <summary>Namespaced extension-catalogue data, keyed by reverse-DNS term id. Round-trips even
    /// when this build has no adapter installed for the extension catalogue.</summary>
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>Catches any other unrecognized property, including legacy root <c>x-*</c> keys, so a
    /// document from a newer minor never silently loses data on read-modify-write.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

/// <summary>Raised when an observation declares a schema major this build does not understand.
/// Carries the raw JSON so the caller can quarantine the document instead of losing it.</summary>
public sealed class UnsupportedQualityObservationMajorException(string message, string rawJson) : Exception(message)
{
    public string RawJson { get; } = rawJson;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static QualityObservationEnvelope Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        ValidateContractVersion(parsed.RootElement, json);
        var observation = JsonSerializer.Deserialize<QualityObservationEnvelope>(json, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(observation);
        return observation;
    }

    public static string Serialize(QualityObservationEnvelope observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    private static void Validate(QualityObservationEnvelope observation)
    {
        if (observation.SchemaVersion != QualityObservationEnvelope.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationEnvelope.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (!observation.ObservationId.StartsWith(QualityObservationEnvelope.ObservationIdPrefix, StringComparison.Ordinal))
            throw new JsonException("Quality observation observationId must be an 'observation-sha256:' identifier.");
        if (observation.Aspects is not { Count: > 0 })
            throw new JsonException("Quality observation must assess at least one aspect.");
        if (observation.Evidence is null || observation.Findings is null)
            throw new JsonException("Quality observation evidence and findings must be present, including when empty.");
    }

    private static void ValidateContractVersion(JsonElement root, string rawJson)
    {
        var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaVersionElement) &&
                             schemaVersionElement.ValueKind == JsonValueKind.Number
            ? schemaVersionElement.GetInt32()
            : -1;
        var schema = root.TryGetProperty("$schema", out var schemaElement) ? schemaElement.GetString() : null;
        if (schemaVersion != QualityObservationEnvelope.CurrentSchemaVersion ||
            !string.Equals(schema, QualityObservationEnvelope.SchemaId, StringComparison.Ordinal))
        {
            throw new UnsupportedQualityObservationMajorException(
                $"Unsupported quality observation schemaVersion '{schemaVersion}'.", rawJson);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter<GradeBand>());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
