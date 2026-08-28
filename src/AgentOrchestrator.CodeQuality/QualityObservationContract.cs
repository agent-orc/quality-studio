using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationTaxonomyRef(string Id, string Version, string Digest);

public sealed record QualityObservationSubject(string UnitId, string ManifestHash);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    string? Agent = null,
    string? Provider = null,
    string? RequestedModel = null,
    string? EffectiveModel = null,
    string? ThinkingLevel = null,
    string? RoutePolicyVersion = null,
    string? RunId = null,
    string? ReviewRunId = null);

public sealed record QualityObservationEvidenceLocator
{
    public string? Path { get; init; }
    public string? SymbolId { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record QualityObservationEvidence(
    string Id,
    QualityEvidenceKind Kind,
    string Summary,
    QualityObservationEvidenceLocator? Locator = null,
    string? ContentHash = null);

public sealed record QualityObservationGrade(int Score, string Band);

public sealed record QualityObservationAspect(
    string AspectId,
    QualityAssessment Assessment,
    string Rationale,
    QualityObservationGrade? Grade = null);

public sealed record QualityObservationFindingSource(
    QualityProducerKind Kind,
    string? ProducerRef = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string AspectId,
    QualitySeverity Severity,
    QualityObservationFindingSource Source,
    string? IssueId = null,
    string? OccurrenceFingerprint = null,
    string? FingerprintAlgorithm = null,
    string? RuleRef = null,
    IReadOnlyList<string>? EvidenceRefs = null)
{
    public IReadOnlyList<string> EvidenceRefs { get; init; } = EvidenceRefs ?? [];
}

public sealed record QualityObservationLegacyProvenance(
    string Schema,
    string Value,
    string SourcePath,
    string Completeness);

public sealed record QualityObservationDocument
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
    public required QualityObservationTaxonomyRef Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public IReadOnlyList<QualityObservationEvidence> Evidence { get; init; } = [];

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public IReadOnlyList<QualityObservationFinding> Findings { get; init; } = [];

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationLegacyProvenance? Legacy { get; init; }

    /// <summary>
    /// Preserves unknown root properties (declared extensions and legacy x-* keys) verbatim
    /// across deserialize/serialize so an unrecognized term never silently disappears.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; init; }
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(QualityObservationDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static QualityObservationDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion)
            throw new JsonException(
                $"Unsupported quality-observation schemaVersion '{document.SchemaVersion}'.");
        return document;
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
}
