using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One producer's immutable result for one exact subject and input set (dossier section 7,
/// "Immutable observation contract"). Nothing here is a writer yet: T2 wires this into the
/// review pipeline as a dual-write append. This type only has to round-trip and validate.
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
    public required ObservationTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required ObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required ObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required ObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required ObservationEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<ObservationEvidence> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<ObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required ObservationAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<ObservationFinding> Findings { get; init; }

    /// <summary>
    /// Legacy root <c>x-*</c> keys only (dossier section 6, "Extension contract"). Anything
    /// else unmapped is a schema violation, not a silently dropped field.
    /// </summary>
    [JsonExtensionData, JsonPropertyOrder(12)]
    public IDictionary<string, JsonElement>? LegacyExtensions { get; init; }
}

public sealed record ObservationTaxonomyReference(
    string Id,
    string Version,
    string Digest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ObservationExtensionCatalogueReference>? ExtensionCatalogues = null);

public sealed record ObservationExtensionCatalogueReference(string Id, string Version, string Digest);

public sealed record ObservationSubject(string UnitId, string ManifestHash);

public sealed record ObservationProfile(string Id, string Version, string PromptHash, string ReviewInputsHash);

public enum ObservationProducerKind
{
    Agent,
    [JsonStringEnumMemberName("deterministic-sensor")] DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

public sealed record ObservationProducer(
    ObservationProducerKind Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null);

public enum ObservationEvidenceStatus { Available, Partial, Unavailable }

public enum ObservationAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    [JsonStringEnumMemberName("not-applicable")] NotApplicable,
    [JsonStringEnumMemberName("not-assessed")] NotAssessed,
}

public enum ObservationChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    [JsonStringEnumMemberName("no-observed-delta")] NoObservedDelta,
    Inconclusive,
}

public enum ObservationDecision { Allow, Warn, Block, Defer }

public enum ObservationLifecycle
{
    Open,
    [JsonStringEnumMemberName("accepted-risk")] AcceptedRisk,
    Waived,
    [JsonStringEnumMemberName("false-positive")] FalsePositive,
    Resolved,
}

public enum ObservationEvidenceKind
{
    [JsonStringEnumMemberName("source-code")] SourceCode,
    [JsonStringEnumMemberName("test-result")] TestResult,
    [JsonStringEnumMemberName("runtime-measurement")] RuntimeMeasurement,
    [JsonStringEnumMemberName("tool-result")] ToolResult,
    Artifact,
    Document,
    [JsonStringEnumMemberName("human-attestation")] HumanAttestation,
}

public sealed record ObservationEvidence(
    string Id,
    ObservationEvidenceKind Kind,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationEvidenceLocator? Locator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record ObservationEvidenceLocator(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null);

public sealed record ObservationGrade(int Score, GradeBand Band);

public sealed record ObservationAspect(
    string AspectId,
    ObservationAssessment Assessment,
    string Rationale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationGrade? Grade = null);

public sealed record ObservationFindingSource(ObservationProducerKind Kind, string ProducerRef);

public sealed record ObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    ObservationFindingSource Source);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(QualityObservation observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservation Deserialize(string json)
    {
        var observation = JsonSerializer.Deserialize<QualityObservation>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        Validate(observation);
        return observation;
    }

    /// <summary>
    /// An unsupported schema major is quarantined, not discarded: the caller keeps the raw
    /// JSON it already had, and this only reports that typed deserialization must not proceed.
    /// </summary>
    public static bool IsSupportedMajor(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("schemaVersion", out var version) &&
               version.ValueKind == JsonValueKind.Number &&
               version.GetInt32() == QualityObservation.CurrentSchemaVersion;
    }

    private static JsonSerializerOptions Create()
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
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void Validate(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservation.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(observation.ObservationId))
            throw new JsonException("A quality observation requires observationId.");
        if (observation.LegacyExtensions?.Keys.Any(key => !key.StartsWith("x-", StringComparison.Ordinal)) == true)
            throw new JsonException("Unrecognized quality observation properties must use the 'x-' legacy prefix.");
    }

    private sealed class GradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("aspects[].grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Core-grade aggregation must not silently fold in a term it does not understand (dossier
/// section 6, "Unknown same-major terms remain visible ... but are excluded from aggregates
/// that do not understand them").
/// </summary>
public static class QualityObservationAggregation
{
    public static IReadOnlyList<ObservationAspect> CoreAspects(
        QualityObservation observation,
        ResolvedTaxonomyCatalogue catalogue,
        IReadOnlyCollection<string>? installedExtensionCatalogueIds = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(catalogue);
        var installed = installedExtensionCatalogueIds ?? [];
        return observation.Aspects.Where(aspect => IsRecognized(aspect.AspectId, catalogue, installed)).ToArray();
    }

    public static IReadOnlyList<ObservationAspect> UnrecognizedAspects(
        QualityObservation observation,
        ResolvedTaxonomyCatalogue catalogue,
        IReadOnlyCollection<string>? installedExtensionCatalogueIds = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(catalogue);
        var installed = installedExtensionCatalogueIds ?? [];
        return observation.Aspects.Where(aspect => !IsRecognized(aspect.AspectId, catalogue, installed)).ToArray();
    }

    private static bool IsRecognized(
        string aspectId, ResolvedTaxonomyCatalogue catalogue, IReadOnlyCollection<string> installedExtensionCatalogueIds)
    {
        if (catalogue.IsCoreAspect(aspectId)) return true;
        var separator = aspectId.IndexOf(':');
        if (separator <= 0) return false;
        var extensionCatalogueId = aspectId[..separator];
        return installedExtensionCatalogueIds.Contains(extensionCatalogueId, StringComparer.Ordinal);
    }
}
