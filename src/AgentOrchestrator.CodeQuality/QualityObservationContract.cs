using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

[JsonConverter(typeof(JsonStringEnumConverter<QualityProducerKind>))]
public enum QualityProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceStatus>))]
public enum QualityEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityAssessment>))]
public enum QualityAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceKind>))]
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

public sealed record QualityTaxonomyRef(string Id, string Version, string Digest)
{
    public static QualityTaxonomyRef Core() =>
        new(QualityTaxonomyCatalogue.CoreCatalogueId, QualityTaxonomyCatalogue.CoreDocument.Version,
            QualityTaxonomyCatalogue.CoreDigest);

    /// <summary>Major version parsed from <see cref="Version"/>, used to reject incompatible catalogues.</summary>
    [JsonIgnore]
    public int Major => int.Parse(Version.Split('.')[0]);
}

public sealed record QualityObservationSubject(string UnitId, ManifestHash ManifestHash);

public sealed record QualityObservationProfile(string Id, string Version, string PromptHash, string ReviewInputsHash);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    string ReviewRunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null);

public sealed record QualityEvidenceLocator(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null);

public sealed record QualityEvidenceItem(
    string Id,
    QualityEvidenceKind Kind,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityEvidenceLocator? Locator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record QualityAspectGrade(int Score, GradeBand Band);

public sealed record QualityAspectObservation(
    string AspectId,
    QualityAssessment Assessment,
    string Rationale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityAspectGrade? Grade = null);

public sealed record QualityObservationFindingSource(QualityProducerKind Kind, string ProducerRef);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityObservationFindingSource Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IssueId = null);

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
    public required QualityTaxonomyRef Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<QualityEvidenceItem> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityAspectObservation> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    /// <summary>
    /// Declared extension-catalogue data, keyed by reverse-DNS extension id. Preserved
    /// unchanged; never merged into core aggregation without an installed adapter.
    /// </summary>
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    /// <summary>
    /// Catches any property this contract does not model yet (including legacy root
    /// <c>x-*</c> keys) so a future field survives an untouched round trip instead of
    /// silently disappearing, unlike the v1/v2 review metadata envelope it replaces.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// Result of reading a document that may carry a taxonomy major version this build does not
/// understand. Unknown majors are quarantined as structured-but-unsupported: the raw JSON stays
/// inspectable and no assessment or grade is inferred from it.
/// </summary>
public sealed record QualityObservationReadResult(
    QualityObservationEnvelope? Observation,
    bool Supported,
    string? QuarantineReason,
    JsonElement RawDocument);

public static class QualityObservationJson
{
    public const int CurrentSchemaVersion = QualityObservationEnvelope.CurrentSchemaVersion;
    public const string SchemaId = QualityObservationEnvelope.SchemaId;
    private const int SupportedTaxonomyMajor = 1;

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

    /// <summary>
    /// Parses a document without throwing on an unsupported schema or taxonomy major. Use this
    /// at ingestion boundaries where a newer or unknown producer's observation must remain
    /// inspectable rather than reject the whole file.
    /// </summary>
    public static QualityObservationReadResult Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.Clone();

        if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            schemaVersionElement.ValueKind != JsonValueKind.Number ||
            schemaVersionElement.GetInt32() != CurrentSchemaVersion)
        {
            return new QualityObservationReadResult(null, false,
                $"Unsupported quality observation schemaVersion '{Describe(schemaVersionElement)}'.", root);
        }

        if (!root.TryGetProperty("taxonomy", out var taxonomyElement) ||
            !taxonomyElement.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String ||
            !TryGetMajor(versionElement.GetString(), out var major) ||
            major != SupportedTaxonomyMajor)
        {
            return new QualityObservationReadResult(null, false,
                $"Unsupported quality taxonomy major in '{Describe(taxonomyElement)}'.", root);
        }

        return new QualityObservationReadResult(Deserialize(json), true, null, root);
    }

    private static bool TryGetMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var separator = version.IndexOf('.', StringComparison.Ordinal);
        var majorText = separator < 0 ? version : version[..separator];
        return int.TryParse(majorText, out major);
    }

    private static string Describe(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined ? "<missing>" : element.ToString();

    private static void Validate(QualityObservationEnvelope observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservationEnvelope.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationEnvelope.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (observation.Taxonomy.Major != SupportedTaxonomyMajor)
            throw new JsonException($"Unsupported quality taxonomy major '{observation.Taxonomy.Version}'.");
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
        options.Converters.Add(new GradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed class GradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException($"Unsupported grade band '{reader.GetString()}'.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
