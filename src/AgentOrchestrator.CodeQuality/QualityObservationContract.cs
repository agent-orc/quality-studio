using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One producer's immutable result for one exact subject and input set. A model rerun
/// creates another observation; it never overwrites or deletes another model's observation.
/// </summary>
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
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QualityTaxonomyReference>? ExtensionTaxonomies { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityObservationEvidence> Evidence { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<QualityAspectObservation> Aspects { get; init; }

    [JsonPropertyOrder(11)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(12)]
    public required IReadOnlyList<QualityFindingObservation> Findings { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonObject? Extensions { get; init; }
}

public sealed record QualityObservationSubject(string UnitId, ManifestHash ManifestHash);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null);

public sealed record QualityEvidenceLocator(JsonObject Value);

public sealed record QualityObservationEvidence
{
    public required string Id { get; init; }

    public required QualityEvidenceKind Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Locator { get; init; }

    public required string Summary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonObject? Extensions { get; init; }
}

public sealed record QualityGrade(int Score, string Band);

public sealed record QualityAspectObservation
{
    public required string AspectId { get; init; }

    public required QualityAssessment Assessment { get; init; }

    public required string Rationale { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public QualityGrade? Grade { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonObject? Extensions { get; init; }
}

public sealed record QualityFindingSource(
    QualityProducerKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerRef = null);

public sealed record QualityFindingObservation
{
    public required string ObservationFindingId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssueId { get; init; }

    public required string OccurrenceFingerprint { get; init; }

    public required string FingerprintAlgorithm { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuleRef { get; init; }

    public required string AspectId { get; init; }

    public required FindingSeverity Severity { get; init; }

    public required IReadOnlyList<string> EvidenceRefs { get; init; }

    public required QualityFindingSource Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonObject? Extensions { get; init; }
}

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
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
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
        if (observation.Evidence is null || observation.Aspects is null || observation.Findings is null)
            throw new JsonException("Quality observation evidence, aspects, and findings must be present, including when empty.");
    }
}
