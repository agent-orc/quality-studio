using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ObservationSubject(string UnitId, string ManifestHash);

public sealed record ObservationProfile(string Id, string Version, string PromptHash, string ReviewInputsHash);

public sealed record ObservationProducer(
    TaxonomyProducerKind Kind,
    string? Agent = null,
    string? Provider = null,
    string? RequestedModel = null,
    string? EffectiveModel = null,
    string? ThinkingLevel = null,
    string? RoutePolicyVersion = null,
    string? RunId = null,
    string? ReviewRunId = null);

public sealed record ObservationEvidenceItem(
    string Id,
    TaxonomyEvidenceKind Kind,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Locator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record ObservationAspectGrade(double Score, string Band);

public sealed record ObservationAspectAssessment(
    string AspectId,
    TaxonomyAssessment Assessment,
    string Rationale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationAspectGrade? Grade = null);

public sealed record ObservationFindingSource(TaxonomyProducerKind Kind, string ProducerRef);

public sealed record ObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    ObservationFindingSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleRef = null);

/// <summary>
/// One producer's immutable result for one exact subject and input set, per the data-model
/// taxonomy dossier (section 7). Never overwritten or deleted by another observation.
/// </summary>
public sealed record QualityObservation
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
    public required TaxonomyRef Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required ObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required ObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required ObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required TaxonomyEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<ObservationEvidenceItem> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<ObservationAspectAssessment> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required TaxonomyAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<ObservationFinding> Findings { get; init; }

    /// <summary>
    /// Root <c>extensions</c> and legacy <c>x-*</c> members. Preserved verbatim so an unknown
    /// taxonomy major or an uninstalled extension catalogue never loses raw data.
    /// </summary>
    [JsonExtensionData, JsonPropertyOrder(12)]
    public Dictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>
    /// False when <see cref="Taxonomy"/> points at the core catalogue but a major version this
    /// build does not understand. The observation itself still round-trips; consumers must
    /// quarantine it as structured-but-unsupported rather than infer a verdict from it.
    /// </summary>
    [JsonIgnore]
    public bool HasSupportedCoreTaxonomy =>
        !string.Equals(Taxonomy.Id, QualityTaxonomyCatalogue.CoreId, StringComparison.Ordinal) ||
        QualityTaxonomyCatalogue.IsSupportedCoreVersion(Taxonomy.Version);
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservation observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservation Deserialize(string json)
    {
        var observation = JsonSerializer.Deserialize<QualityObservation>(json, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(observation);
        return observation;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void Validate(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservation.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(observation.ObservationId))
            throw new JsonException("Quality observation observationId is required.");
    }
}
