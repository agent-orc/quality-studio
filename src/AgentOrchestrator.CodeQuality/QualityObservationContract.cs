using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

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
    public IReadOnlyList<QualityTaxonomyReference> ExtensionTaxonomies { get; init; } = [];

    [JsonPropertyOrder(6)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityReviewProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityProducer Producer { get; init; }

    [JsonPropertyOrder(9)]
    public required string EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<QualityEvidence> Evidence { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityAspectAssessment> Aspects { get; init; }

    [JsonPropertyOrder(12)]
    public required string Assessment { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityDecision? Decision { get; init; }

    [JsonPropertyOrder(14)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityLegacyReference? Legacy { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record QualityTaxonomyReference(
    string Id,
    string Version,
    string Digest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationSubject(
    string UnitId,
    string ManifestHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityReviewProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityProducer(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RoutePolicyVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidence(
    string Id,
    string Kind,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityEvidenceLocator? Locator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentType = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Payload = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidenceLocator(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uri = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StartColumn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndLine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EndColumn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectAssessment(
    string AspectId,
    string Axis,
    string Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityObservationGrade? Grade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(
    int Score,
    string Band,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityDecision(
    string Value,
    string PolicyRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    string Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IssueId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FingerprintAliases = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Recommendation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityFindingSource(
    string Kind,
    string ProducerRef,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityLegacyReference(
    string Schema,
    string SourcePath,
    string Completeness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationReadResult(
    bool IsSupported,
    QualityObservationDocument? Observation,
    JsonElement Raw,
    string? UnsupportedReason);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static readonly Lazy<JsonSerializerOptions> CompactOptions = new(() => new JsonSerializerOptions(Options)
    {
        WriteIndented = false,
    });

    public static string Serialize(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static string SerializeLine(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, CompactOptions.Value);
    }

    public static QualityObservationDocument Deserialize(string json)
    {
        var result = Read(json);
        return result.Observation ?? throw new JsonException(result.UnsupportedReason);
    }

    public static QualityObservationReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            return new QualityObservationReadResult(false, null, raw, "A quality observation must be a JSON object.");
        if (!raw.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !schemaVersion.TryGetInt32(out var major) ||
            major != QualityObservationDocument.CurrentSchemaVersion)
        {
            var displayedVersion = schemaVersion.ValueKind == JsonValueKind.Undefined
                ? "missing"
                : schemaVersion.GetRawText();
            return new QualityObservationReadResult(false, null, raw,
                $"Unsupported quality observation schemaVersion '{displayedVersion}'.");
        }
        if (!raw.TryGetProperty("$schema", out var schema) ||
            !string.Equals(schema.GetString(), QualityObservationDocument.SchemaId, StringComparison.Ordinal))
        {
            return new QualityObservationReadResult(false, null, raw, "Unsupported quality observation schema identity.");
        }
        if (raw.TryGetProperty("taxonomy", out var taxonomy) &&
            taxonomy.TryGetProperty("id", out var taxonomyId) &&
            string.Equals(taxonomyId.GetString(), QualityTaxonomyTerms.CoreId, StringComparison.Ordinal) &&
            taxonomy.TryGetProperty("version", out var taxonomyVersion) &&
            TryMajor(taxonomyVersion.GetString(), out var taxonomyMajor) &&
            taxonomyMajor != QualityTaxonomyTerms.SupportedCoreMajor)
        {
            return new QualityObservationReadResult(false, null, raw,
                $"Unsupported core taxonomy major '{taxonomyMajor}'.");
        }

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(raw, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        Validate(observation);
        return new QualityObservationReadResult(true, observation, raw, null);
    }

    private static void Validate(QualityObservationDocument observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (observation.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");
        if (!QualityTaxonomyTerms.EvidenceStatuses.Contains(observation.EvidenceStatus))
            throw new JsonException($"Unknown core evidenceStatus '{observation.EvidenceStatus}'.");
        if (!QualityTaxonomyTerms.Assessments.Contains(observation.Assessment))
            throw new JsonException($"Unknown core assessment '{observation.Assessment}'.");
        if (!QualityTaxonomyTerms.ProducerKinds.Contains(observation.Producer.Kind))
            throw new JsonException($"Unknown core producer kind '{observation.Producer.Kind}'.");
        if (observation.Decision is not null &&
            (string.IsNullOrWhiteSpace(observation.Decision.PolicyRef) ||
             !QualityTaxonomyTerms.Decisions.Contains(observation.Decision.Value)))
            throw new JsonException("A decision requires a core value and non-empty policyRef.");
        if (observation.Aspects.Count == 0)
            throw new JsonException("A quality observation requires at least one aspect.");
    }

    private static bool TryMajor(string? version, out int major)
    {
        major = -1;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var separator = version.IndexOf('.');
        return separator > 0 && int.TryParse(version[..separator], NumberStyles.None,
            CultureInfo.InvariantCulture, out major);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new UtcObservationTimestampConverter());
        return options;
    }

    private sealed class UtcObservationTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                throw new JsonException("observedAt must be a UTC ISO 8601 instant.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero) throw new JsonException("observedAt must be a UTC instant.");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}
