using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationDocument : QualityExtensibleContract
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
    public IReadOnlyList<QualityTaxonomyReference> ExtensionTaxonomies { get; init; } = [];

    [JsonPropertyOrder(5)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required QualityReviewProfile Profile { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityTerm EvidenceStatus { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<QualityEvidenceReference> Evidence { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<QualityAspectObservation> Aspects { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTerm? Assessment { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTerm? Change { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTerm? Decision { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyRef { get; init; }

    [JsonPropertyOrder(15)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityLegacyReference? Legacy { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? LegacyRootExtensions { get; init; }
}

public sealed record QualityTaxonomyReference : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Digest { get; init; }
}

public sealed record QualityObservationSubject : QualityExtensibleContract
{
    public required string UnitId { get; init; }

    public required string ManifestHash { get; init; }
}

public sealed record QualityReviewProfile : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string PromptHash { get; init; }

    public required string ReviewInputsHash { get; init; }
}

public sealed record QualityObservationProducer : QualityExtensibleContract
{
    public required QualityTerm Kind { get; init; }

    public required string Agent { get; init; }

    public required string Provider { get; init; }

    public required string RequestedModel { get; init; }

    public required string EffectiveModel { get; init; }

    public required string ThinkingLevel { get; init; }

    public required string RoutePolicyVersion { get; init; }

    public required string RunId { get; init; }

    public required string ReviewRunId { get; init; }
}

public sealed record QualityEvidenceReference : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required QualityTerm Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityEvidenceLocator? Locator { get; init; }

    public required string Summary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentHash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaType { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawContent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalText { get; init; }
}

public sealed record QualityEvidenceLocator : QualityExtensibleContract
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SymbolId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactRef { get; init; }
}

public sealed record QualityAspectObservation : QualityExtensibleContract
{
    public required string AspectId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTerm? Assessment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityTerm? Change { get; init; }

    public required string Rationale { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationGrade? Grade { get; init; }
}

public sealed record QualityObservationGrade : QualityExtensibleContract
{
    public required int Score { get; init; }

    public required string Band { get; init; }
}

public sealed record QualityObservationFinding : QualityExtensibleContract
{
    public required string ObservationFindingId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IssueId { get; init; }

    public required string OccurrenceFingerprint { get; init; }

    public required string FingerprintAlgorithm { get; init; }

    public required string RuleRef { get; init; }

    public required string AspectId { get; init; }

    public required QualityTerm Severity { get; init; }

    public required IReadOnlyList<string> EvidenceRefs { get; init; }

    public required QualityFindingSource Source { get; init; }
}

public sealed record QualityFindingSource : QualityExtensibleContract
{
    public required QualityTerm Kind { get; init; }

    public required string ProducerRef { get; init; }
}

public sealed record QualityLegacyReference : QualityExtensibleContract
{
    public required string Schema { get; init; }

    public required string Value { get; init; }

    public required string SourcePath { get; init; }

    public required QualityTerm Completeness { get; init; }
}

public enum QualityProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

public enum QualityEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

public enum QualityAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

public enum QualityChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

public enum QualityDecision
{
    Allow,
    Warn,
    Block,
    Defer,
}

public enum QualitySeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public enum QualityLifecycleState
{
    Open,
    AcceptedRisk,
    Waived,
    FalsePositive,
    Resolved,
}

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

public enum QualityImportCompleteness
{
    Complete,
    Partial,
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = QualityTaxonomyJson.CreateOptions();

    public static string Serialize(QualityObservationDocument observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityContractReadResult<QualityObservationDocument> Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        var schemaVersion = QualityTaxonomyJson.ReadSchemaVersion(raw, "Quality observation");
        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
        {
            return new QualityContractReadResult<QualityObservationDocument>(
                schemaVersion,
                false,
                null,
                raw,
                $"Unsupported quality observation schemaVersion '{schemaVersion}'.");
        }

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(raw, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        Validate(observation);
        return new QualityContractReadResult<QualityObservationDocument>(
            schemaVersion, true, observation, raw, null);
    }

    private static void Validate(QualityObservationDocument observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (observation.Assessment is null && observation.Change is null)
            throw new JsonException("A quality observation requires an assessment or change result.");
        if (observation.Decision is not null && string.IsNullOrWhiteSpace(observation.PolicyRef))
            throw new JsonException("A quality observation decision requires policyRef.");
        if (observation.Evidence is null || observation.Aspects is null || observation.Findings is null)
            throw new JsonException("Quality observation evidence, aspects, and findings are required.");
        foreach (var aspect in observation.Aspects)
        {
            if ((aspect.Assessment is null) == (aspect.Change is null))
                throw new JsonException($"Aspect '{aspect.AspectId}' requires exactly one assessment axis value.");
        }
        foreach (var key in observation.LegacyRootExtensions?.Keys ?? [])
        {
            if (!key.StartsWith("x-", StringComparison.Ordinal))
                throw new JsonException($"Unknown quality observation root property '{key}' is not a legacy x-* extension.");
        }
    }
}
