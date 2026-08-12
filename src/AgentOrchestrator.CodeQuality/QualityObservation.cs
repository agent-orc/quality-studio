using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

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
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityCatalogueReference Taxonomy { get; init; }

    [JsonPropertyOrder(5)]
    public IReadOnlyList<QualityCatalogueReference> ExtensionCatalogues { get; init; } = [];

    [JsonPropertyOrder(6)]
    public required QualitySubject Subject { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityProducer Producer { get; init; }

    [JsonPropertyOrder(9)]
    public required string EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public IReadOnlyList<QualityEvidence> Evidence { get; init; } = [];

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityAspectAssessment> Aspects { get; init; }

    [JsonPropertyOrder(12)]
    public required string Assessment { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityDecision? Decision { get; init; }

    [JsonPropertyOrder(14)]
    public IReadOnlyList<QualityObservationFinding> Findings { get; init; } = [];

    [JsonPropertyOrder(15)]
    public string Completeness { get; init; } = "complete";

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityLegacyReference? Legacy { get; init; }

    [JsonPropertyOrder(17), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

public sealed record QualitySubject(
    string UnitId,
    string ManifestHash,
    string? Scope = null,
    string? Path = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    string? Kind = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityProducer(
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
    string? UsageRunId = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    int? Line = null,
    int? Column = null,
    string? ArtifactRef = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidence(
    string Id,
    string Kind,
    string Summary,
    QualityEvidenceLocator? Locator = null,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Raw = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(
    int Score,
    string Band,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectAssessment(
    string AspectId,
    string Assessment,
    string? Change = null,
    string? Rationale = null,
    QualityObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityDecision(
    string Value,
    string PolicyRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityFindingSource(
    string Kind,
    string ProducerRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    string Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    IReadOnlyList<string>? FingerprintAliases = null,
    string? Title = null,
    string? Description = null,
    string? Recommendation = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityLegacyReference(
    string Schema,
    string SourcePath,
    string ImportCompleteness,
    JsonElement? Value = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationReadResult(
    bool Supported,
    int? SchemaVersion,
    QualityObservation? Observation,
    JsonElement Raw);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateSupported(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationReadResult Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var raw = document.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("A quality observation must be a JSON object.");
        var schemaVersion = raw.TryGetProperty("schemaVersion", out var version) && version.TryGetInt32(out var value)
            ? value
            : (int?)null;
        var schema = raw.TryGetProperty("$schema", out var schemaNode) ? schemaNode.GetString() : null;
        if (schemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(schema, QualityObservation.SchemaId, StringComparison.Ordinal))
            return new QualityObservationReadResult(false, schemaVersion, null, raw);

        if (!raw.TryGetProperty("taxonomy", out var taxonomy) ||
            !taxonomy.TryGetProperty("id", out var taxonomyId) ||
            !taxonomy.TryGetProperty("version", out var taxonomyVersion) ||
            !string.Equals(taxonomyId.GetString(), QualityTaxonomy.CoreId, StringComparison.Ordinal) ||
            !Version.TryParse(taxonomyVersion.GetString(), out var parsedTaxonomyVersion) ||
            parsedTaxonomyVersion.Major != 1)
            return new QualityObservationReadResult(false, schemaVersion, null, raw);

        var observation = raw.Deserialize<QualityObservation>(Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        ValidateSupported(observation);
        return new QualityObservationReadResult(true, schemaVersion, observation, raw);
    }

    public static string CreateObservationId(params string[] identityParts)
    {
        ArgumentNullException.ThrowIfNull(identityParts);
        if (identityParts.Length == 0 || identityParts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Observation identity parts cannot be empty.", nameof(identityParts));
        var canonical = string.Join('\0', identityParts);
        return "observation-sha256:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateSupported(QualityObservation observation)
    {
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservation.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomy.CoreId, StringComparison.Ordinal) ||
            !Version.TryParse(observation.Taxonomy.Version, out var version) || version.Major != 1)
            throw new JsonException($"Unsupported quality taxonomy '{observation.Taxonomy.Id}@{observation.Taxonomy.Version}'.");
    }
}
