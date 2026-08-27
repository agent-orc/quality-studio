using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationEnvelope
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string ObservationId { get; init; }

    [JsonPropertyOrder(3)]
    public required ObservationTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public IReadOnlyList<ObservationTaxonomyReference> ExtensionCatalogues { get; init; } = [];

    [JsonPropertyOrder(5)]
    public required ObservationSubject Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required ObservationProfile Profile { get; init; }

    [JsonPropertyOrder(7)]
    public required ObservationProducer Producer { get; init; }

    [JsonPropertyOrder(8)]
    public required EvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<ObservationEvidenceItem> Evidence { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<ObservationAspectResult> Aspects { get; init; }

    [JsonPropertyOrder(11)]
    public required Assessment Assessment { get; init; }

    [JsonPropertyOrder(12)]
    public required IReadOnlyList<ObservationFindingResult> Findings { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationLegacyProvenance? Legacy { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ObservationTaxonomyReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string Digest);

public sealed record ObservationSubject(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] ManifestHash ManifestHash);

public sealed record ObservationProfile(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string PromptHash,
    [property: JsonPropertyOrder(3)] string ReviewInputsHash);

[JsonConverter(typeof(JsonStringEnumConverter<ObservationProducerKind>))]
public enum ObservationProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

public sealed record ObservationProducer
{
    [JsonPropertyOrder(0)]
    public required ObservationProducerKind Kind { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; init; }

    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestedModel { get; init; }

    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EffectiveModel { get; init; }

    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingLevel { get; init; }

    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoutePolicyVersion { get; init; }

    [JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewRunId { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceStatus>))]
public enum EvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<Assessment>))]
public enum Assessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityChange>))]
public enum QualityChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<Decision>))]
public enum Decision
{
    Allow,
    Warn,
    Block,
    Defer,
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceKind>))]
public enum EvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

public sealed record ObservationEvidenceItem
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1)]
    public required EvidenceKind Kind { get; init; }

    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Locator { get; init; }

    [JsonPropertyOrder(3)]
    public required string Summary { get; init; }

    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentHash { get; init; }

    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; init; }

    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRaw { get; init; }

    [JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ObservationAspectResult
{
    [JsonPropertyOrder(0)]
    public required string AspectId { get; init; }

    [JsonPropertyOrder(1)]
    public required Assessment Assessment { get; init; }

    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rationale { get; init; }

    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationGrade? Grade { get; init; }

    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ObservationGrade(
    [property: JsonPropertyOrder(0)] double Score,
    [property: JsonPropertyOrder(1)] string Band);

public sealed record ObservationFindingResult
{
    [JsonPropertyOrder(0)]
    public required string ObservationFindingId { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssueId { get; init; }

    [JsonPropertyOrder(2)]
    public required string OccurrenceFingerprint { get; init; }

    [JsonPropertyOrder(3)]
    public required string FingerprintAlgorithm { get; init; }

    [JsonPropertyOrder(4)]
    public required string RuleRef { get; init; }

    [JsonPropertyOrder(5)]
    public required string AspectId { get; init; }

    [JsonPropertyOrder(6)]
    public required FindingSeverity Severity { get; init; }

    [JsonPropertyOrder(7)]
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    [JsonPropertyOrder(8)]
    public required ObservationFindingSource Source { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record ObservationFindingSource(
    [property: JsonPropertyOrder(0)] ObservationProducerKind Kind,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerRef = null);

[JsonConverter(typeof(JsonStringEnumConverter<LegacyCompleteness>))]
public enum LegacyCompleteness
{
    Complete,
    Partial,
}

public sealed record ObservationLegacyProvenance(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string Value,
    [property: JsonPropertyOrder(2)] string SourcePath,
    [property: JsonPropertyOrder(3)] LegacyCompleteness Completeness);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationEnvelope observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationEnvelope Deserialize(string json)
    {
        var observation = JsonSerializer.Deserialize<QualityObservationEnvelope>(json, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(observation);
        return observation;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void Validate(QualityObservationEnvelope observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservationEnvelope.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationEnvelope.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(observation.ObservationId))
            throw new JsonException("Quality observation observationId is required.");
    }
}
