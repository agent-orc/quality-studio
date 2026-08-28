using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One producer's immutable result for one exact subject and input set (quality-observation.v1).
/// Append-only: a later observation never overwrites or deletes an earlier one. See
/// docs/operations/data-model-taxonomy/index.html section 7.
/// </summary>
public sealed record QualityObservationDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    /// <summary>The only taxonomy major this build understands. A different major is quarantined, not read.</summary>
    public const int SupportedTaxonomyMajor = 1;

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string ObservationId { get; init; }

    [JsonPropertyOrder(3)]
    public required TaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required ObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required ObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required ObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<ObservationEvidenceItem> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<ObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<ObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>Root <c>x-*</c> keys, preserved as legacy extensions only; never a home for new fields.</summary>
    [JsonExtensionData, JsonPropertyOrder(13)]
    public IDictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

public sealed record ObservationSubject(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] string ManifestHash);

public sealed record ObservationProfile(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string PromptHash,
    [property: JsonPropertyOrder(3)] string ReviewInputsHash);

/// <summary>Every field is required; a runner that cannot report one writes "unknown", never a default.</summary>
public sealed record ObservationProducer(
    [property: JsonPropertyOrder(0)] QualityProducerKind Kind,
    [property: JsonPropertyOrder(1)] string Agent,
    [property: JsonPropertyOrder(2)] string Provider,
    [property: JsonPropertyOrder(3)] string RequestedModel,
    [property: JsonPropertyOrder(4)] string EffectiveModel,
    [property: JsonPropertyOrder(5)] string ThinkingLevel,
    [property: JsonPropertyOrder(6)] string RoutePolicyVersion,
    [property: JsonPropertyOrder(7)] string RunId,
    [property: JsonPropertyOrder(8)] string ReviewRunId);

public sealed record ObservationEvidenceItem(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] QualityEvidenceKind Kind,
    [property: JsonPropertyOrder(2)] IReadOnlyDictionary<string, JsonElement> Locator,
    [property: JsonPropertyOrder(3)] string Summary,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record ObservationGrade(
    [property: JsonPropertyOrder(0)] int Score,
    [property: JsonPropertyOrder(1)] GradeBand Band);

public sealed record ObservationAspect(
    [property: JsonPropertyOrder(0)] string AspectId,
    [property: JsonPropertyOrder(1)] QualityAssessment Assessment,
    [property: JsonPropertyOrder(2)] string Rationale,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationGrade? Grade = null);

public sealed record ObservationFindingSource(
    [property: JsonPropertyOrder(0)] QualityProducerKind Kind,
    [property: JsonPropertyOrder(1)] string ProducerRef);

public sealed record ObservationFinding(
    [property: JsonPropertyOrder(0)] string ObservationFindingId,
    [property: JsonPropertyOrder(1)] string OccurrenceFingerprint,
    [property: JsonPropertyOrder(2)] string FingerprintAlgorithm,
    [property: JsonPropertyOrder(3)] string AspectId,
    [property: JsonPropertyOrder(4)] FindingSeverity Severity,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> EvidenceRefs,
    [property: JsonPropertyOrder(6)] ObservationFindingSource Source,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IssueId = null,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleRef = null);

/// <summary>
/// A document whose taxonomy major this build does not understand. It is quarantined as
/// structured-but-unsupported: the raw JSON is retained for inspection and no verdict or grade
/// is inferred from it. See the acceptance invariants in the taxonomy dossier, section 12.
/// </summary>
public sealed class UnsupportedTaxonomyMajorException(string taxonomyId, string taxonomyVersion, string rawJson)
    : Exception($"Observation taxonomy '{taxonomyId}'@{taxonomyVersion} major is not supported by this build.")
{
    public string TaxonomyId { get; } = taxonomyId;
    public string TaxonomyVersion { get; } = taxonomyVersion;
    public string RawJson { get; } = rawJson;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument document)
    {
        ValidateContractVersion(document);
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>
    /// Deserializes and validates. Throws <see cref="UnsupportedTaxonomyMajorException"/>, carrying
    /// the original JSON, when the document's taxonomy major is not one this build understands.
    /// </summary>
    public static QualityObservationDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        ValidateContractVersion(document);
        ValidateTaxonomyMajor(document, json);
        return document;
    }

    private static void ValidateContractVersion(QualityObservationDocument document)
    {
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{document.SchemaVersion}'.");
    }

    private static void ValidateTaxonomyMajor(QualityObservationDocument document, string rawJson)
    {
        var major = ParseMajor(document.Taxonomy.Version)
            ?? throw new JsonException($"Observation taxonomy version '{document.Taxonomy.Version}' is not valid SemVer.");
        if (major != QualityObservationDocument.SupportedTaxonomyMajor)
            throw new UnsupportedTaxonomyMajorException(document.Taxonomy.Id, document.Taxonomy.Version, rawJson);
    }

    private static int? ParseMajor(string semVer)
    {
        var separator = semVer.IndexOf('.');
        return separator > 0 && int.TryParse(semVer[..separator], out var major) ? major : null;
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
        options.Converters.Add(new GradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed class GradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
