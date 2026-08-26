using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }
public enum QualityEvidenceStatus { Available, Partial, Unavailable }
public enum QualityAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }
public enum QualityDecisionValue { Allow, Warn, Block, Defer }
public enum QualityEvidenceKind { SourceCode, TestResult, RuntimeMeasurement, ToolResult, Artifact, Document, HumanAttestation }

public sealed record QualityCatalogueReference(
    string Id,
    string Version,
    string Digest,
    string? Prefix = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyReference(
    string Id,
    string Version,
    string Digest,
    IReadOnlyList<QualityCatalogueReference> Extensions,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null);

public sealed record QualityObservationSubject(
    string UnitId,
    string ManifestHash,
    string? Scope = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityReviewProfile(
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
    string? RunId = null,
    string? ReviewRunId = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    string? Uri = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidence(
    string Id,
    QualityEvidenceKind Kind,
    string Summary,
    QualityEvidenceLocator? Locator = null,
    string? ArtifactReference = null,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Content = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(
    int Score,
    GradeBand Band,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectAssessment(
    string AspectId,
    QualityAssessment Assessment,
    string Rationale,
    QualityObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityPolicyDecision(
    QualityDecisionValue Value,
    string PolicyRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityFindingSource(
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
    QualityFindingSource Source,
    IReadOnlyList<string>? FingerprintAliases = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationLegacy(
    string Schema,
    JsonElement Value,
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
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityReviewProfile Profile { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityEvidence> Evidence { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<QualityAspectAssessment> Aspects { get; init; }

    [JsonPropertyOrder(11)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityPolicyDecision? Decision { get; init; }

    [JsonPropertyOrder(13)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationLegacy? Legacy { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public enum QualityObservationReadStatus { Supported, UnsupportedSchemaMajor, UnsupportedTaxonomyMajor }

public sealed record QualityObservationReadResult(
    QualityObservationReadStatus Status,
    JsonElement Raw,
    QualityObservationDocument? Observation,
    int SchemaMajor,
    int? TaxonomyMajor)
{
    public bool IsSupported => Status == QualityObservationReadStatus.Supported;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CompactOptions { get; } = new(Options) { WriteIndented = false };

    public static string Serialize(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static string SerializeCompact(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, CompactOptions);
    }

    public static QualityObservationReadResult Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("Quality observation must be a JSON object.");

        var schemaMajor = raw.TryGetProperty("schemaVersion", out var schemaVersion) &&
                          schemaVersion.TryGetInt32(out var parsedSchemaVersion)
            ? parsedSchemaVersion
            : throw new JsonException("Quality observation schemaVersion must be an integer.");
        if (schemaMajor != QualityObservationDocument.CurrentSchemaVersion)
            return new QualityObservationReadResult(
                QualityObservationReadStatus.UnsupportedSchemaMajor, raw, null, schemaMajor, null);

        var taxonomyMajor = ReadTaxonomyMajor(raw);
        if (taxonomyMajor != 1)
            return new QualityObservationReadResult(
                QualityObservationReadStatus.UnsupportedTaxonomyMajor, raw, null, schemaMajor, taxonomyMajor);

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(raw.GetRawText(), Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(observation);
        return new QualityObservationReadResult(
            QualityObservationReadStatus.Supported, raw, observation, schemaMajor, taxonomyMajor);
    }

    public static string CreateObservationId(params string[] identityParts)
    {
        ArgumentNullException.ThrowIfNull(identityParts);
        if (identityParts.Length == 0 || identityParts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Observation identity parts must be non-empty.", nameof(identityParts));
        var canonical = string.Join('\0', identityParts);
        return "observation-sha256:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static int ReadTaxonomyMajor(JsonElement root)
    {
        if (!root.TryGetProperty("taxonomy", out var taxonomy) ||
            !taxonomy.TryGetProperty("version", out var versionElement))
            throw new JsonException("Quality observation taxonomy.version is required.");
        var version = versionElement.GetString();
        if (version is null || !int.TryParse(version.Split('.', 2)[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var major))
            throw new JsonException("Quality observation taxonomy.version must be semantic versioning.");
        return major;
    }

    private static void Validate(QualityObservationDocument observation)
    {
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomyDocument.CoreId, StringComparison.Ordinal))
            throw new JsonException("Quality observation must reference the quality-studio/core taxonomy.");
        if (observation.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");

        var evidenceIds = observation.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (evidenceIds.Count != observation.Evidence.Count)
            throw new JsonException("Quality observation evidence ids must be unique.");
        if (observation.Findings.SelectMany(finding => finding.EvidenceRefs)
            .Any(reference => !evidenceIds.Contains(reference)))
            throw new JsonException("Quality observation finding evidenceRefs must resolve within the observation.");
        if (observation.Decision is not null && string.IsNullOrWhiteSpace(observation.Decision.PolicyRef))
            throw new JsonException("Quality observation decisions require policyRef.");
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

    private sealed class QualityGradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var value)
                ? value
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
