using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityObservationProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

public enum QualityObservationEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

public enum QualityObservationAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

public enum QualityObservationEvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

public sealed record QualityTaxonomyReference(string Id, string Version, string Digest);

public sealed record QualityObservationSubject(string UnitId, string ManifestHash);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash);

public sealed record QualityObservationProducer(
    QualityObservationProducerKind Kind,
    string RunId,
    string? Agent = null,
    string? Provider = null,
    string? RequestedModel = null,
    string? EffectiveModel = null,
    string? ThinkingLevel = null,
    string? RoutePolicyVersion = null,
    string? ReviewRunId = null);

public sealed record QualityObservationEvidenceLocator(string Path, string? SymbolId = null);

public sealed record QualityObservationEvidence(
    string Id,
    QualityObservationEvidenceKind Kind,
    string Summary,
    QualityObservationEvidenceLocator? Locator = null,
    string? ContentHash = null);

public sealed record QualityObservationGrade(int Score, string Band);

public sealed record QualityObservationAspect(
    string AspectId,
    QualityObservationAssessment Assessment,
    string Rationale,
    QualityObservationGrade? Grade = null);

public sealed record QualityObservationFindingSource(QualityObservationProducerKind Kind, string ProducerRef);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityObservationFindingSource Source);

/// <summary>
/// One producer's immutable result for one exact subject and input set. Never overwritten;
/// a rerun (even with the same model) appends another observation. See section 7 of
/// docs/operations/data-model-taxonomy/index.html.
/// </summary>
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
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(5)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<QualityObservationEvidence> Evidence { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(10)]
    public required QualityObservationAssessment Assessment { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    /// <summary>
    /// Namespaced extension data, preserved byte-for-byte across deserialize/serialize.
    /// Excluded from core aggregation unless the matching extension catalogue is installed.
    /// </summary>
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

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

    public static string ComputeObservationId(
        string runId, string unitId, string kind, string subjectHash, string inputHash, string taxonomyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var canonical = string.Join('\0', runId, unitId, kind, subjectHash, inputHash, taxonomyDigest);
        return "observation-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void Validate(QualityObservationEnvelope observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservationEnvelope.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationEnvelope.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(observation.ObservationId) ||
            !observation.ObservationId.StartsWith("observation-sha256:", StringComparison.Ordinal))
            throw new JsonException("Quality observation observationId must be an 'observation-sha256:' identifier.");
        if (observation.Evidence is null || observation.Aspects is null || observation.Findings is null)
            throw new JsonException(
                "Quality observation evidence, aspects, and findings must be present, including when empty.");
    }
}

public enum QualityTaxonomyCompatibility
{
    Supported,
    UnsupportedTaxonomyMajor,
}

/// <summary>
/// Compares an observation's pinned taxonomy against an installed catalogue without discarding
/// the observation: an unsupported major is quarantined, never dropped or reinterpreted.
/// </summary>
public static class QualityObservationCompatibility
{
    public static QualityTaxonomyCompatibility Evaluate(
        QualityTaxonomyReference observed, ResolvedQualityTaxonomyCatalogue installed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(installed);
        return string.Equals(observed.Id, installed.CatalogueId, StringComparison.Ordinal) &&
               MajorOf(observed.Version) == MajorOf(installed.CatalogueVersion)
            ? QualityTaxonomyCompatibility.Supported
            : QualityTaxonomyCompatibility.UnsupportedTaxonomyMajor;
    }

    /// <summary>
    /// Aspects whose id is registered in the installed catalogue. Extension aspects (unknown
    /// terms from an uninstalled catalogue) round-trip on the observation but are excluded here.
    /// </summary>
    public static IReadOnlyList<QualityObservationAspect> CoreAspects(
        QualityObservationEnvelope observation, ResolvedQualityTaxonomyCatalogue installed)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(installed);
        return observation.Aspects.Where(aspect => installed.HasAspect(aspect.AspectId)).ToArray();
    }

    private static int MajorOf(string version) =>
        int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);
}
