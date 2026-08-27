using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ObservationSubject(string UnitId, string ManifestHash);

public sealed record ObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string? ReviewInputsHash = null);

public sealed record ObservationProducer(
    ProducerKind Kind,
    string? Agent = null,
    string? Provider = null,
    string? RequestedModel = null,
    string? EffectiveModel = null,
    string? ThinkingLevel = null,
    string? RoutePolicyVersion = null,
    string? RunId = null,
    string? ReviewRunId = null);

public sealed record ObservationEvidenceLocator(
    string? Path = null,
    FindingRange? Range = null,
    string? SymbolId = null,
    string? ArtifactRef = null);

public sealed record ObservationEvidence(
    string Id,
    ObservationEvidenceKind Kind,
    ObservationEvidenceLocator Locator,
    string Summary,
    string? ContentHash = null);

public sealed record ObservationAspectResult(
    string AspectId,
    ObservationAssessment Assessment,
    string Rationale,
    ReviewGrade? Grade = null);

public sealed record ObservationFindingSource(ProducerKind Kind, string ProducerRef);

public sealed record ObservationFinding(
    string ObservationFindingId,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    ObservationFindingSource Source,
    string? IssueId = null,
    string? OccurrenceFingerprint = null,
    string? FingerprintAlgorithm = null,
    string? RuleRef = null);

/// <summary>
/// One producer's immutable result for one exact subject and input set. Every field pins
/// the taxonomy, profile, and producer identity that produced it; a later run never
/// overwrites or reinterprets an existing observation, it only appends another one.
/// </summary>
public sealed record QualityObservationDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string ObservationId { get; init; }

    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<QualityTaxonomyReference>? ExtensionCatalogues { get; init; }

    public required ObservationSubject Subject { get; init; }

    public required ObservationProfile Profile { get; init; }

    public required ObservationProducer Producer { get; init; }

    public required EvidenceStatus EvidenceStatus { get; init; }

    public required IReadOnlyList<ObservationEvidence> Evidence { get; init; }

    public required IReadOnlyList<ObservationAspectResult> Aspects { get; init; }

    public required ObservationAssessment Assessment { get; init; }

    public required IReadOnlyList<ObservationFinding> Findings { get; init; }

    /// <summary>
    /// Explicit, preserved extension data. Unlike the legacy <c>review-meta.v2</c> root
    /// <c>x-*</c> keys, this is a named contract member so it round-trips without a
    /// dedicated converter and is never silently dropped by unmapped-member handling.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>
/// The result of parsing a document that declares a <c>schemaVersion</c>. An unsupported
/// major is quarantined: <see cref="Document"/> is null and no assessment is inferred, but
/// <see cref="Raw"/> keeps every original field inspectable.
/// </summary>
public sealed record QualityObservationParseResult(
    bool Supported,
    QualityObservationDocument? Document,
    JsonElement Raw,
    int SchemaVersion);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static QualityObservationDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(document);
        return document;
    }

    /// <summary>
    /// Parses without throwing on an unsupported schema major. Raw JSON is always
    /// preserved so an unknown-major document can still be inspected and never appears
    /// as though it produced a pass.
    /// </summary>
    public static QualityObservationParseResult Parse(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement.Clone();
        var schemaVersion = root.TryGetProperty("schemaVersion", out var version) &&
                             version.ValueKind == JsonValueKind.Number
            ? version.GetInt32()
            : 0;
        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
        {
            return new QualityObservationParseResult(false, null, root, schemaVersion);
        }

        var document = JsonSerializer.Deserialize<QualityObservationDocument>(root.GetRawText(), Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(document);
        return new QualityObservationParseResult(true, document, root, schemaVersion);
    }

    private static void Validate(QualityObservationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality observation schemaVersion '{document.SchemaVersion}'.");
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in document.Evidence)
        {
            if (!evidenceIds.Add(evidence.Id))
            {
                throw new JsonException($"Duplicate evidence id '{evidence.Id}'.");
            }
        }

        foreach (var finding in document.Findings)
        {
            foreach (var evidenceRef in finding.EvidenceRefs)
            {
                if (!evidenceIds.Contains(evidenceRef))
                {
                    throw new JsonException(
                        $"Finding '{finding.ObservationFindingId}' references unknown evidence id '{evidenceRef}'.");
                }
            }
        }
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
        options.Converters.Add(new GradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    /// <summary>
    /// <see cref="GradeBand"/> keeps its established upper-case A-F spelling
    /// (<see cref="ReviewMetaJson"/> uses the same convention); the generic kebab-case
    /// converter would otherwise lower-case it to "d".
    /// </summary>
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
