using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public static class QualityTaxonomyTerms
{
    public const string CatalogueId = "quality-studio/core";
    public const string CatalogueVersion = "1.0.0";
    public const string ProducerAgent = "agent";
    public const string ProducerDeterministicSensor = "deterministic-sensor";
    public const string ProducerHuman = "human";
    public const string ProducerImported = "imported";
    public const string ProducerUnknown = "unknown";
    public const string EvidenceAvailable = "available";
    public const string EvidencePartial = "partial";
    public const string EvidenceUnavailable = "unavailable";
    public const string AssessmentPass = "pass";
    public const string AssessmentConcern = "concern";
    public const string AssessmentFail = "fail";
    public const string AssessmentInconclusive = "inconclusive";
    public const string AssessmentNotApplicable = "not-applicable";
    public const string AssessmentNotAssessed = "not-assessed";
}

public sealed record QualityTaxonomyDocument
{
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;
    [JsonPropertyOrder(1)] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyOrder(2)] public required string Id { get; init; }
    [JsonPropertyOrder(3)] public required string Version { get; init; }
    [JsonPropertyOrder(4)] public required string Prefix { get; init; }
    [JsonPropertyOrder(5)] public required IReadOnlyList<QualityTaxonomyAxis> Axes { get; init; }
    [JsonPropertyOrder(6)] public required IReadOnlyList<QualityAspectTerm> Aspects { get; init; }
    [JsonPropertyOrder(99)] public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityTaxonomyAxis
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<QualityTaxonomyTerm> Terms { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityTaxonomyTerm
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public bool Deprecated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Replacement { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public sealed record QualityAspectTerm
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> AllowedAxes { get; init; } = [];
    public bool Deprecated { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Replacement { get; init; }
    public Dictionary<string, JsonElement> Extensions { get; init; } = [];
}

public static class QualityTaxonomyCatalogue
{
    private const string ResourceSuffix = ".catalogues.quality-taxonomy.core.v1.json";
    private static readonly Lazy<byte[]> Bytes = new(ReadBytes);
    private static readonly Lazy<QualityTaxonomyDocument> DocumentValue = new(() =>
        JsonSerializer.Deserialize<QualityTaxonomyDocument>(Bytes.Value, QualityObservationJson.Options)
        ?? throw new InvalidDataException("The embedded quality taxonomy catalogue is empty."));

    public static QualityTaxonomyDocument Core => DocumentValue.Value;
    public static string Digest => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Bytes.Value));

    private static byte[] ReadBytes()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(candidate =>
            candidate.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (name is null) throw new InvalidDataException("The embedded quality taxonomy catalogue was not found.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("The embedded quality taxonomy catalogue could not be opened.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

public sealed record LegacySemanticMapping(
    string? Assessment = null,
    string? Change = null,
    string? Decision = null,
    string? EvidenceStatus = null,
    string? PolicyRef = null,
    string? Lifecycle = null);

/// <summary>Pure, exhaustive adapters from the legacy spellings approved in the taxonomy dossier.</summary>
public static class QualityLegacyMapping
{
    private static readonly IReadOnlyDictionary<string, string> AspectAliases =
        QualityTaxonomyCatalogue.Core.Aspects
            .SelectMany(aspect => aspect.Aliases.Select(alias => (alias, aspect.Id)))
            .ToDictionary(pair => pair.alias, pair => pair.Id, StringComparer.Ordinal);

    public static LegacySemanticMapping SecurityVerdict(string value) => value switch
    {
        "pass" => new(QualityTaxonomyTerms.AssessmentPass, Decision: "allow",
            EvidenceStatus: QualityTaxonomyTerms.EvidenceAvailable, PolicyRef: "security-sensor-agent-v1"),
        "warn" => new(QualityTaxonomyTerms.AssessmentConcern, Decision: "warn",
            EvidenceStatus: QualityTaxonomyTerms.EvidenceAvailable, PolicyRef: "security-sensor-agent-v1"),
        "block" => new(QualityTaxonomyTerms.AssessmentFail, Decision: "block",
            EvidenceStatus: QualityTaxonomyTerms.EvidenceAvailable, PolicyRef: "security-sensor-agent-v1"),
        "unavailable" => new(QualityTaxonomyTerms.AssessmentInconclusive, Decision: "defer",
            EvidenceStatus: QualityTaxonomyTerms.EvidenceUnavailable, PolicyRef: "security-sensor-agent-v1"),
        _ => throw Unknown("security verdict", value),
    };

    public static LegacySemanticMapping FlowVerdict(string value) => value switch
    {
        "pass" => new(QualityTaxonomyTerms.AssessmentPass),
        "fail" => new(QualityTaxonomyTerms.AssessmentFail),
        "undetermined" => new(QualityTaxonomyTerms.AssessmentInconclusive),
        _ => throw Unknown("flow verdict", value),
    };

    public static LegacySemanticMapping AttackVerdict(string value) => value switch
    {
        "pass" => new(QualityTaxonomyTerms.AssessmentPass),
        "finding" => new(QualityTaxonomyTerms.AssessmentFail),
        "not-applicable" => new(QualityTaxonomyTerms.AssessmentNotApplicable),
        "not-yet-checked" => new(QualityTaxonomyTerms.AssessmentNotAssessed),
        _ => throw Unknown("attack verdict", value),
    };

    public static LegacySemanticMapping ChangeSummary(string value) => value switch
    {
        "no-quality-delta" => new(Change: "no-observed-delta"),
        "improved" => new(Change: "improved"),
        "neutral" => new(Change: "unchanged"),
        "regression" => new(Change: "regressed"),
        _ => throw Unknown("change summary", value),
    };

    public static LegacySemanticMapping ChangeAspect(string value) => value switch
    {
        "good" => new(QualityTaxonomyTerms.AssessmentPass),
        "mixed" => new(QualityTaxonomyTerms.AssessmentConcern),
        "concerning" => new(QualityTaxonomyTerms.AssessmentFail),
        "unknown" => new(QualityTaxonomyTerms.AssessmentInconclusive),
        _ => throw Unknown("change aspect", value),
    };

    public static LegacySemanticMapping FindingState(string value) => value switch
    {
        "open" => new(Lifecycle: "open"),
        "accepted" or "accepted-risk" => new(Lifecycle: "accepted-risk"),
        "waived" => new(Lifecycle: "waived"),
        "falsePositive" or "false-positive" => new(Lifecycle: "false-positive"),
        "resolved" => new(Lifecycle: "resolved"),
        _ => throw Unknown("finding state", value),
    };

    public static string Aspect(string legacyId) =>
        AspectAliases.TryGetValue(legacyId, out var mapped) ? mapped : legacyId;

    public static QualityEvidence Evidence(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return new QualityEvidence
            {
                Id = id,
                Kind = "tool-result",
                Summary = "Structured legacy evidence.",
                ContentHash = hash,
                MediaType = "application/json",
                Content = parsed.RootElement.Clone(),
            };
        }
        catch (JsonException)
        {
            return new QualityEvidence
            {
                Id = id,
                Kind = "document",
                Summary = value,
                ContentHash = hash,
                MediaType = "text/plain",
                Content = JsonSerializer.SerializeToElement(value),
            };
        }
    }

    private static ArgumentOutOfRangeException Unknown(string contract, string value) =>
        new(nameof(value), value, $"Unknown legacy {contract} value.");
}
