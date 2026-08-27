using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public static class QualityTaxonomyConstants
{
    public const string CatalogueSchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string ObservationSchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
    public const string CoreCatalogueId = "quality-studio/core";
    public const string CoreCatalogueVersion = "1.0.0";
    public const int SchemaVersion = 1;
}

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    string? ReplacedBy = null);

public sealed record QualityTaxonomyAxis(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    IReadOnlyList<string> AllowedAxes,
    string? ReplacedBy = null);

public sealed record QualityTaxonomyCatalogue(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    string Description,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects);

public static class QualityTaxonomyCatalogueLoader
{
    private const string CoreResourceSuffix = "catalogues.quality-studio-core.v1.json";
    private static readonly Lazy<(QualityTaxonomyCatalogue Catalogue, string Digest)> CoreValue = new(LoadCore);

    public static QualityTaxonomyCatalogue Core => CoreValue.Value.Catalogue;
    public static string CoreDigest => CoreValue.Value.Digest;

    private static (QualityTaxonomyCatalogue Catalogue, string Digest) LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogueLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded taxonomy resource '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The core taxonomy catalogue must be a JSON object.");
        Validate(catalogue);
        return (catalogue, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void Validate(QualityTaxonomyCatalogue catalogue)
    {
        if (catalogue.SchemaVersion != QualityTaxonomyConstants.SchemaVersion ||
            !string.Equals(catalogue.Schema, QualityTaxonomyConstants.CatalogueSchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{catalogue.SchemaVersion}'.");
        if (!string.Equals(catalogue.Id, QualityTaxonomyConstants.CoreCatalogueId, StringComparison.Ordinal) ||
            !string.Equals(catalogue.Version, QualityTaxonomyConstants.CoreCatalogueVersion, StringComparison.Ordinal))
            throw new JsonException($"Unexpected core taxonomy identity '{catalogue.Id}@{catalogue.Version}'.");

        RequireUniqueOrdered(catalogue.Axes.Select(axis => (axis.Id, axis.Order)), "taxonomy axes");
        RequireUniqueOrdered(catalogue.Aspects.Select(aspect => (aspect.Id, aspect.Order)), "taxonomy aspects");
        foreach (var axis in catalogue.Axes)
            RequireUniqueOrdered(axis.Terms.Select(term => (term.Id, term.Order)), $"terms for axis '{axis.Id}'");

        var axisIds = catalogue.Axes.Select(axis => axis.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var aspect in catalogue.Aspects)
        {
            if (aspect.AllowedAxes.Any(axis => !axisIds.Contains(axis)))
                throw new JsonException($"Aspect '{aspect.Id}' refers to an unknown assessment axis.");
        }
    }

    private static void RequireUniqueOrdered(IEnumerable<(string Id, int Order)> values, string description)
    {
        var entries = values.ToArray();
        if (entries.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != entries.Length)
            throw new JsonException($"The {description} contain duplicate identifiers.");
        if (entries.Select(value => value.Order).Distinct().Count() != entries.Length ||
            !entries.Select(value => value.Order).SequenceEqual(Enumerable.Range(0, entries.Length)))
            throw new JsonException($"The {description} must have unique contiguous ordering from zero.");
    }
}

public enum QualityProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }
public enum QualityEvidenceStatus { Available, Partial, Unavailable }
public enum QualityAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }
public enum QualityChange { Improved, Regressed, Mixed, Unchanged, NoObservedDelta, Inconclusive }
public enum QualityDecisionValue { Allow, Warn, Block, Defer }
public enum QualityEvidenceKind { SourceCode, TestResult, RuntimeMeasurement, ToolResult, Artifact, Document, HumanAttestation }
public enum QualityCompleteness { Complete, Partial }

public sealed record QualityTaxonomyReference
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationSubject
{
    public required string UnitId { get; init; }
    public required string ManifestHash { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationProfile
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string PromptHash { get; init; }
    public required string ReviewInputsHash { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationProducer
{
    public required QualityProducerKind Kind { get; init; }
    public string? Agent { get; init; }
    public required string Provider { get; init; }
    public required string RequestedModel { get; init; }
    public required string EffectiveModel { get; init; }
    public required string ThinkingLevel { get; init; }
    public required string RoutePolicyVersion { get; init; }
    public string? RunId { get; init; }
    public string? ReviewRunId { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityEvidenceLocator
{
    public string? Path { get; init; }
    public string? SymbolId { get; init; }
    public string? Uri { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityEvidenceItem
{
    public required string Id { get; init; }
    public required QualityEvidenceKind Kind { get; init; }
    public QualityEvidenceLocator? Locator { get; init; }
    public string? ArtifactReference { get; init; }
    public required string Summary { get; init; }
    public string? ContentHash { get; init; }
    public string? ContentType { get; init; }
    public JsonElement? Raw { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationGrade
{
    public required int Score { get; init; }
    public required string Band { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationAspect
{
    public required string AspectId { get; init; }
    public required QualityAssessment Assessment { get; init; }
    public QualityChange? Change { get; init; }
    public required string Rationale { get; init; }
    public QualityObservationGrade? Grade { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationDecision
{
    public required QualityDecisionValue Value { get; init; }
    public required string PolicyRef { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityFindingSource
{
    public required QualityProducerKind Kind { get; init; }
    public required string ProducerRef { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationFinding
{
    public required string ObservationFindingId { get; init; }
    public required string IssueId { get; init; }
    public required string OccurrenceFingerprint { get; init; }
    public required string FingerprintAlgorithm { get; init; }
    public IReadOnlyList<string> FingerprintAliases { get; init; } = [];
    public required string RuleRef { get; init; }
    public required string AspectId { get; init; }
    public required FindingSeverity Severity { get; init; }
    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];
    public required QualityFindingSource Source { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationLegacy
{
    public required string Schema { get; init; }
    public required JsonElement Value { get; init; }
    public required string SourcePath { get; init; }
    public required QualityCompleteness Completeness { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityObservationDocument
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = QualityTaxonomyConstants.ObservationSchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = QualityTaxonomyConstants.SchemaVersion;

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
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(9)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public IReadOnlyList<QualityEvidenceItem> Evidence { get; init; } = [];

    [JsonPropertyOrder(11)]
    public IReadOnlyList<QualityObservationAspect> Aspects { get; init; } = [];

    [JsonPropertyOrder(12)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(13)]
    public QualityChange? Change { get; init; }

    [JsonPropertyOrder(14)]
    public QualityObservationDecision? Decision { get; init; }

    [JsonPropertyOrder(15)]
    public IReadOnlyList<QualityObservationFinding> Findings { get; init; } = [];

    [JsonPropertyOrder(16)]
    public QualityObservationLegacy? Legacy { get; init; }

    [JsonPropertyOrder(17)]
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> LegacyRootExtensions { get; init; } = [];
}

public sealed record QualityObservationReadResult(
    QualityObservationDocument? Observation,
    int SchemaVersion,
    JsonElement Raw)
{
    public bool IsSupported => Observation is not null;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static QualityObservationReadResult Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A quality observation must be a JSON object.");
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var version))
            throw new JsonException("A quality observation must declare an integer schemaVersion.");

        var raw = parsed.RootElement.Clone();
        if (version != QualityTaxonomyConstants.SchemaVersion)
            return new QualityObservationReadResult(null, version, raw);

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        ValidateSupportedVersion(observation);
        return new QualityObservationReadResult(observation, version, raw);
    }

    public static QualityObservationDocument Deserialize(string json)
    {
        var result = Read(json);
        return result.Observation ?? throw new JsonException(
            $"Unsupported quality observation schemaVersion '{result.SchemaVersion}'. Raw data remains available through Read().");
    }

    public static string Serialize(QualityObservationDocument observation)
    {
        ValidateSupportedVersion(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    private static void ValidateSupportedVersion(QualityObservationDocument observation)
    {
        if (observation.SchemaVersion != QualityTaxonomyConstants.SchemaVersion ||
            !string.Equals(observation.Schema, QualityTaxonomyConstants.ObservationSchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomyConstants.CoreCatalogueId, StringComparison.Ordinal) ||
            !string.Equals(observation.Taxonomy.Version, QualityTaxonomyConstants.CoreCatalogueVersion, StringComparison.Ordinal))
            throw new JsonException($"Unsupported core taxonomy '{observation.Taxonomy.Id}@{observation.Taxonomy.Version}'.");
        if (observation.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");
        if (observation.LegacyRootExtensions.Keys.Any(key => !key.StartsWith("x-", StringComparison.Ordinal)))
            throw new JsonException("Legacy root extensions must use the x- prefix.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed record LegacyQualityProjection(
    QualityAssessment? Assessment = null,
    QualityChange? Change = null,
    QualityEvidenceStatus? EvidenceStatus = null,
    QualityDecisionValue? Decision = null,
    string? PolicyRef = null,
    string? CanonicalLifecycle = null);

public static class LegacyQualityTaxonomyMapper
{
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    public static LegacyQualityProjection MapSecurityVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass, EvidenceStatus: QualityEvidenceStatus.Available,
            Decision: QualityDecisionValue.Allow, PolicyRef: SecurityPolicyRef),
        "warn" => new(QualityAssessment.Concern, EvidenceStatus: QualityEvidenceStatus.Available,
            Decision: QualityDecisionValue.Warn, PolicyRef: SecurityPolicyRef),
        "block" => new(QualityAssessment.Fail, EvidenceStatus: QualityEvidenceStatus.Available,
            Decision: QualityDecisionValue.Block, PolicyRef: SecurityPolicyRef),
        "unavailable" => new(QualityAssessment.Inconclusive, EvidenceStatus: QualityEvidenceStatus.Unavailable,
            PolicyRef: SecurityPolicyRef),
        _ => throw Unknown("security verdict", value),
    };

    public static LegacyQualityProjection MapFlowVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass),
        "fail" => new(QualityAssessment.Fail),
        "undetermined" => new(QualityAssessment.Inconclusive),
        _ => throw Unknown("flow verdict", value),
    };

    public static LegacyQualityProjection MapAttackVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass),
        "finding" => new(QualityAssessment.Fail),
        "not-applicable" => new(QualityAssessment.NotApplicable),
        "not-yet-checked" => new(QualityAssessment.NotAssessed),
        _ => throw Unknown("attack verdict", value),
    };

    public static LegacyQualityProjection MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => new(Change: QualityChange.NoObservedDelta),
        "improved" => new(Change: QualityChange.Improved),
        "neutral" => new(Change: QualityChange.Unchanged),
        "regression" => new(Change: QualityChange.Regressed),
        _ => throw Unknown("change summary", value),
    };

    public static LegacyQualityProjection MapChangeAspect(string value) => value switch
    {
        "good" => new(QualityAssessment.Pass),
        "mixed" => new(QualityAssessment.Concern),
        "concerning" => new(QualityAssessment.Fail),
        "unknown" => new(QualityAssessment.Inconclusive),
        _ => throw Unknown("change aspect verdict", value),
    };

    public static LegacyQualityProjection MapFindingState(string value) => value switch
    {
        "open" => new(CanonicalLifecycle: "open"),
        "accepted" or "accepted-risk" => new(CanonicalLifecycle: "accepted-risk"),
        "waived" => new(CanonicalLifecycle: "waived"),
        "falsePositive" or "false-positive" => new(CanonicalLifecycle: "false-positive"),
        "resolved" => new(CanonicalLifecycle: "resolved"),
        _ => throw Unknown("finding state", value),
    };

    public static string? MapAspect(string value, string? mappedAnalyzerAspect = null, string? producerNamespace = null) => value switch
    {
        "correctness" => "code.correctness",
        "architecture" => "code.architecture",
        "security" => "security.general",
        "secrets" => "security.secrets",
        "dependencies" => "security.dependencies",
        "authentication-authorization" => "security.authentication-authorization",
        "input-validation" => "security.input-validation",
        "configuration-iac" => "security.configuration-iac",
        "boundaries" => "security.boundary-exposure",
        "performance" => "performance.general",
        "risk" => "change.risk",
        "test-evidence" => "change.test-evidence",
        "scope-discipline" => "change.scope-discipline",
        "architecture-drift" => "change.architecture-drift",
        "sensor-availability" => null,
        "analyzer" when !string.IsNullOrWhiteSpace(mappedAnalyzerAspect) => mappedAnalyzerAspect,
        "analyzer" when !string.IsNullOrWhiteSpace(producerNamespace) => $"{producerNamespace}:analyzer",
        _ => throw Unknown("aspect", value),
    };

    public static QualityEvidenceItem MapEvidenceString(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return new QualityEvidenceItem
            {
                Id = id,
                Kind = QualityEvidenceKind.ToolResult,
                Summary = "Preserved legacy JSON evidence.",
                ContentHash = hash,
                ContentType = "application/json",
                Raw = parsed.RootElement.Clone(),
            };
        }
        catch (JsonException)
        {
            using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return new QualityEvidenceItem
            {
                Id = id,
                Kind = QualityEvidenceKind.Document,
                Summary = "Preserved legacy text evidence.",
                ContentHash = hash,
                ContentType = "text/plain",
                Raw = parsed.RootElement.Clone(),
            };
        }
    }

    private static ArgumentOutOfRangeException Unknown(string contract, string value) =>
        new(nameof(value), value, $"Unknown legacy {contract}; preserve the raw value instead of coercing it.");
}
