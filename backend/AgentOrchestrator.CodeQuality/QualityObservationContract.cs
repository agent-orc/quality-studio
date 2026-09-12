using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One producer's immutable result for one exact subject and input set. Observations are appended,
/// never replaced: a rerun with another model adds a record instead of overwriting the previous one.
/// Current sidecars and reports are projections selected from these records.
/// </summary>
public sealed record QualityObservation
{
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string ObservationId { get; init; }

    [JsonPropertyOrder(3)]
    public required DateTimeOffset RecordedAt { get; init; }

    [JsonPropertyOrder(4)]
    public required TaxonomyCatalogueReference Taxonomy { get; init; }

    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TaxonomyExtensionCatalogueReference>? ExtensionCatalogues { get; init; }

    [JsonPropertyOrder(6)]
    public required ObservationSubject Subject { get; init; }

    [JsonPropertyOrder(7)]
    public required ObservationProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required ObservationProducer Producer { get; init; }

    /// <summary>Coverage of the evidence behind this observation. Never a pass and never a block.</summary>
    [JsonPropertyOrder(9)]
    public required string EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public required string Assessment { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Change { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationGrade? Grade { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyOrder(14)]
    public IReadOnlyList<ObservationEvidence> Evidence { get; init; } = [];

    [JsonPropertyOrder(15)]
    public IReadOnlyList<ObservationAspect> Aspects { get; init; } = [];

    [JsonPropertyOrder(16)]
    public IReadOnlyList<ObservationFinding> Findings { get; init; } = [];

    /// <summary>Versioned policy dispositions. A decision is never derived from an assessment.</summary>
    [JsonPropertyOrder(17)]
    public IReadOnlyList<ObservationPolicyOutcome> PolicyOutcomes { get; init; } = [];

    [JsonPropertyOrder(18), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationLegacyOrigin? Legacy { get; init; }

    [JsonPropertyOrder(19), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonNode>? Extensions { get; init; }

    /// <summary>Root <c>x-*</c> keys of older documents. They round-trip unchanged instead of being dropped.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

public sealed record ObservationSubject(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] string ManifestHash,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null);

public sealed record ObservationProfile(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PromptHash = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewInputsHash = null);

/// <summary>
/// Who produced the observation. <c>kind</c> is always explicit; a missing value is
/// <see cref="Unknown"/> and is never defaulted to an agent or backfilled from today's policy.
/// </summary>
public sealed record ObservationProducer(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedModel = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveModel = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RoutePolicyVersion = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SensorId = null,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SensorVersion = null,
    [property: JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null)
{
    /// <summary>The value stored when a fact could not be established. It is never inferred later.</summary>
    public const string Unknown = "unknown";
}

public sealed record ObservationGrade(
    [property: JsonPropertyOrder(0)] int Score,
    [property: JsonPropertyOrder(1)] string Band,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null);

public sealed record ObservationEvidence(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Kind,
    [property: JsonPropertyOrder(2)] string Summary,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationLocator? Locator = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactRef = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MediaType = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Content = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonNode>? Extensions = null);

public sealed record ObservationLocator(
    [property: JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FindingRange? Range = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Uri = null);

public sealed record ObservationAspect(
    [property: JsonPropertyOrder(0)] string AspectId,
    [property: JsonPropertyOrder(1)] string Assessment,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationGrade? Grade = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EvidenceRefs = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonNode>? Extensions = null);

public sealed record ObservationFinding(
    [property: JsonPropertyOrder(0)] string ObservationFindingId,
    [property: JsonPropertyOrder(1)] string AspectId,
    [property: JsonPropertyOrder(2)] string Severity,
    [property: JsonPropertyOrder(3)] ObservationFindingSource Source,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IssueId = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OccurrenceFingerprint = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FingerprintAlgorithm = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? LegacyFingerprints = null,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleRef = null,
    [property: JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Recommendation = null,
    [property: JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? EvidenceRefs = null,
    [property: JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonNode>? Extensions = null);

public sealed record ObservationFindingSource(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerRef = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SensorId = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SensorVersion = null)
{
    /// <summary>Marks a finding the observation's own producer authored.</summary>
    public const string Self = "self";
}

public sealed record ObservationPolicyOutcome(
    [property: JsonPropertyOrder(0)] string PolicyRef,
    [property: JsonPropertyOrder(1)] string Decision,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LegacyValue = null);

public sealed record ObservationLegacyOrigin(
    [property: JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] string SourcePath,
    [property: JsonPropertyOrder(2)] string Completeness,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImportId = null)
{
    public const string Complete = "complete";
    public const string Partial = "partial";
}

/// <summary>Why a stored record could not be interpreted as a supported observation.</summary>
public enum ObservationSupport
{
    Supported,
    UnsupportedSchemaVersion,
    UnsupportedTaxonomyMajor,
    Malformed,
}

/// <summary>
/// A stored line together with its support state. The raw JSON is always retained so an
/// unsupported record stays inspectable and no verdict or grade is inferred from it.
/// </summary>
public sealed record QualityObservationRecord(
    ObservationSupport Support,
    string RawJson,
    QualityObservation? Observation = null,
    string? Reason = null)
{
    public bool IsSupported => Support == ObservationSupport.Supported && Observation is not null;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    /// <summary>Single-line form used by the append-only observation ledger.</summary>
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(indented: false);

    public static string Serialize(QualityObservation observation, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Validate(observation);
        return JsonSerializer.Serialize(observation, indented ? Options : LineOptions);
    }

    public static QualityObservation Deserialize(string json)
    {
        var observation = JsonSerializer.Deserialize<QualityObservation>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        Validate(observation);
        return observation;
    }

    /// <summary>
    /// Reads a stored record without throwing. An unsupported schema version or taxonomy major is
    /// quarantined as structured-but-unsupported instead of being reinterpreted or dropped.
    /// </summary>
    public static QualityObservationRecord Read(string json, QualityTaxonomyResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        QualityObservation observation;
        try
        {
            observation = Deserialize(json);
        }
        catch (JsonException exception)
        {
            var version = TryReadSchemaVersion(json);
            return new(
                version is not null && version != QualityObservation.CurrentSchemaVersion
                    ? ObservationSupport.UnsupportedSchemaVersion
                    : ObservationSupport.Malformed,
                json,
                Reason: exception.Message);
        }

        var effective = resolver ?? QualityTaxonomyResolver.Default;
        return effective.SupportsTaxonomy(observation.Taxonomy)
            ? new(ObservationSupport.Supported, json, observation)
            : new(ObservationSupport.UnsupportedTaxonomyMajor, json, Reason:
                $"Taxonomy '{observation.Taxonomy.Id}@{observation.Taxonomy.Version}' is not installed.");
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcMillisecondConverter());
        return options;
    }

    private static void Validate(QualityObservation observation)
    {
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservation.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (observation.RecordedAt.Offset != TimeSpan.Zero)
            throw new JsonException("recordedAt must be a UTC instant.");
        if (!QualityTaxonomyJson.IsSemanticVersion(observation.Taxonomy.Version))
            throw new JsonException("taxonomy.version must be a Semantic Version.");

        foreach (var key in observation.LegacyExtensions?.Keys ?? [])
        {
            if (!key.StartsWith("x-", StringComparison.Ordinal))
                throw new JsonException(
                    $"'{key}' is not a known observation member. Extension data belongs in 'extensions'.");
        }
    }

    private static int? TryReadSchemaVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("schemaVersion", out var version) &&
                   version.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class UtcMillisecondConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return value is not null && value.EndsWith('Z') &&
                   DateTimeOffset.TryParseExact(value, Format, CultureInfo.InvariantCulture,
                       DateTimeStyles.AssumeUniversal, out var timestamp)
                ? timestamp
                : throw new JsonException("recordedAt must use UTC ISO 8601 with millisecond precision.");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero) throw new JsonException("recordedAt must be a UTC instant.");
            writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}
