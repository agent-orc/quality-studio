using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

public enum QualityEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

public enum QualityAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

public enum QualityEvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

public sealed record QualityTaxonomyReference(string Id, string Version, string Digest);

public sealed record QualityObservationSubject(string UnitId, string ManifestHash);

public sealed record QualityObservationProfile(
    string Id, string Version, string PromptHash, string ReviewInputsHash);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RoutePolicyVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null);

public sealed record QualityObservationEvidence(
    string Id,
    QualityEvidenceKind Kind,
    JsonElement Locator,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record QualityGrade(int Score, string Band);

public sealed record QualityAspectResult(
    string AspectId,
    QualityAssessment Assessment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityGrade? Grade = null);

public sealed record QualityObservationFindingSource(QualityProducerKind Kind, string ProducerRef);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityObservationFindingSource Source);

/// <summary>
/// One producer's immutable result for one exact subject and input set (dossier section 7). A
/// model rerun never overwrites another run's observation; it appends a new one.
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

    [JsonPropertyOrder(4)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<QualityObservationEvidence> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityAspectResult> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    /// <summary>Extension terms declared by an installed catalogue (dossier section 6, extension contract).</summary>
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>
    /// Root <c>x-*</c> keys and any other unmapped property. Preserved so a future field or a
    /// legacy extension round-trips instead of silently disappearing (dossier finding F7).
    /// </summary>
    [JsonExtensionData, JsonPropertyOrder(13)]
    public IDictionary<string, JsonElement>? LegacyExtensionData { get; init; }
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

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

    private static JsonSerializerOptions Create()
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
        if (observation.Evidence is null || observation.Aspects is null || observation.Findings is null)
            throw new JsonException(
                "Quality observation evidence/aspects/findings must be present, including when empty.");
        if (!observation.ObservationId.StartsWith("observation-sha256:", StringComparison.Ordinal))
            throw new JsonException("Quality observation observationId must be an 'observation-sha256:' identifier.");
    }
}
