using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyReference(
    string Id,
    string Version,
    string Digest) : QualityExtensibleObject;

public sealed record QualityObservationSubject(
    string UnitId,
    string Scope,
    string ManifestHash,
    string InputHash,
    string? Path = null) : QualityExtensibleObject;

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash) : QualityExtensibleObject;

public sealed record QualityObservationProducer(
    string Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    string? ModelRevision = null,
    string? ReviewRunId = null,
    string? UsageRunId = null) : QualityExtensibleObject;

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    string? Uri = null,
    int? StartLine = null,
    int? StartColumn = null,
    int? EndLine = null,
    int? EndColumn = null) : QualityExtensibleObject;

public sealed record QualityEvidence(
    string Id,
    string Kind,
    string Summary,
    QualityEvidenceLocator? Locator = null,
    string? ArtifactReference = null,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Raw = null) : QualityExtensibleObject;

public sealed record QualityObservationGrade(
    int Score,
    string Band) : QualityExtensibleObject;

public sealed record QualityObservationAspect(
    string AspectId,
    string Axis,
    string Assessment,
    string Rationale,
    QualityObservationGrade? Grade = null) : QualityExtensibleObject;

public sealed record QualityFindingSource(
    string Kind,
    string ProducerRef) : QualityExtensibleObject;

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    IReadOnlyList<string> LegacyFingerprints,
    string RuleRef,
    string AspectId,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    string? IssueId = null) : QualityExtensibleObject;

public sealed record QualityPolicyDecision(
    string Value,
    string PolicyRef) : QualityExtensibleObject;

public sealed record QualityLegacyReference(
    string Schema,
    string SourcePath,
    string Completeness,
    JsonElement? Value = null) : QualityExtensibleObject;

public sealed record QualityObservationDocument(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string ObservationId,
    [property: JsonPropertyOrder(3)] DateTimeOffset ObservedAt,
    [property: JsonPropertyOrder(4)] QualityTaxonomyReference Taxonomy,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityTaxonomyReference> ExtensionTaxonomies,
    [property: JsonPropertyOrder(6)] QualityObservationSubject Subject,
    [property: JsonPropertyOrder(7)] QualityObservationProfile Profile,
    [property: JsonPropertyOrder(8)] QualityObservationProducer Producer,
    [property: JsonPropertyOrder(9)] string EvidenceStatus,
    [property: JsonPropertyOrder(10)] IReadOnlyList<QualityEvidence> Evidence,
    [property: JsonPropertyOrder(11)] IReadOnlyList<QualityObservationAspect> Aspects,
    [property: JsonPropertyOrder(12)] string Assessment,
    [property: JsonPropertyOrder(13)] IReadOnlyList<QualityObservationFinding> Findings,
    [property: JsonPropertyOrder(14)] string Completeness,
    [property: JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityPolicyDecision? Decision = null,
    [property: JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityLegacyReference? Legacy = null)
    : QualityExtensibleObject
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
}

public sealed record QualityObservationReadResult(
    int SchemaVersion,
    bool Supported,
    QualityObservationDocument? Document,
    JsonElement Raw);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateSupported(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static QualityObservationReadResult Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object ||
            !raw.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
            throw new JsonException("A quality observation must contain an integer schemaVersion.");

        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
            return new QualityObservationReadResult(schemaVersion, false, null, raw);

        var document = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        ValidateSupported(document);
        return new QualityObservationReadResult(schemaVersion, true, document, raw);
    }

    public static QualityObservationDocument Deserialize(string json)
    {
        var result = Read(json);
        return result.Document ?? throw new JsonException(
            $"Unsupported quality observation schemaVersion '{result.SchemaVersion}'. Raw JSON is available through Read().");
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private static void ValidateSupported(QualityObservationDocument document)
    {
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{document.SchemaVersion}'.");
        if (document.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");
    }
}
