using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }
public enum QualityEvidenceStatus { Available, Partial, Unavailable }
public enum QualityAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }
public enum QualityChange { Improved, Regressed, Mixed, Unchanged, NoObservedDelta, Inconclusive }
public enum QualityDecision { Allow, Warn, Block, Defer }
public enum QualityLifecycle { Open, AcceptedRisk, Waived, FalsePositive, Resolved }
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

public sealed record QualityTaxonomyReference(
    string Id,
    string Version,
    string Digest,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationSubject(
    string UnitId,
    string ManifestHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string? ModelRevision = null,
    string? RunId = null,
    string? ReviewRunId = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    string? ArtifactRef = null,
    string? Uri = null,
    int? Line = null,
    int? Column = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationEvidence(
    string Id,
    QualityEvidenceKind Kind,
    QualityEvidenceLocator Locator,
    string Summary,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Payload = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(int Score, GradeBand Band);

public sealed record QualityObservationAspect(
    string AspectId,
    QualityAssessment Assessment,
    string Rationale,
    QualityObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFindingSource(
    QualityProducerKind Kind,
    string ProducerRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityObservationFindingSource Source,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityPolicyDecision(
    QualityDecision Value,
    string PolicyRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationLegacy(
    string Schema,
    string Value,
    string SourcePath,
    string Completeness,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

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
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public IReadOnlyList<QualityTaxonomyReference> ExtensionCatalogues { get; init; } = [];

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
    public required IReadOnlyList<QualityObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(11)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityChange? Change { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityPolicyDecision? Decision { get; init; }

    [JsonPropertyOrder(14)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationLegacy? Legacy { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement> LegacyExtensions { get; init; } = new Dictionary<string, JsonElement>();
}

public enum QualityObservationReadStatus { Supported, UnsupportedSchemaVersion }

public sealed record QualityObservationReadResult(
    QualityObservationReadStatus Status,
    JsonElement RawDocument,
    QualityObservationDocument? Observation);

public sealed class UnsupportedQualityObservationException(
    int schemaVersion,
    JsonElement rawDocument)
    : JsonException($"Unsupported quality observation schemaVersion '{schemaVersion}'.")
{
    public int SchemaVersion { get; } = schemaVersion;
    public JsonElement RawDocument { get; } = rawDocument;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument observation)
    {
        ValidateSupported(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationReadResult Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object ||
            !raw.TryGetProperty("schemaVersion", out var versionNode) ||
            !versionNode.TryGetInt32(out var version))
        {
            throw new JsonException("Quality observation must have an integer schemaVersion.");
        }

        if (version != QualityObservationDocument.CurrentSchemaVersion)
        {
            return new QualityObservationReadResult(
                QualityObservationReadStatus.UnsupportedSchemaVersion,
                raw,
                null);
        }

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(raw, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        ValidateSupported(observation);
        return new QualityObservationReadResult(QualityObservationReadStatus.Supported, raw, observation);
    }

    public static QualityObservationDocument Deserialize(string json)
    {
        var result = Read(json);
        if (result.Observation is null)
        {
            throw new UnsupportedQualityObservationException(
                result.RawDocument.GetProperty("schemaVersion").GetInt32(),
                result.RawDocument);
        }

        return result.Observation;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new QualityGradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void ValidateSupported(QualityObservationDocument observation)
    {
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        }

        var unknownLegacyExtension = observation.LegacyExtensions.Keys
            .FirstOrDefault(key => !key.StartsWith("x-", StringComparison.Ordinal));
        if (unknownLegacyExtension is not null)
        {
            throw new JsonException($"Unknown quality observation property '{unknownLegacyExtension}'.");
        }
    }

    private sealed class QualityGradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
