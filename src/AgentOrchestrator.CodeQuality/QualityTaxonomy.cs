using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public static class QualityTaxonomy
{
    public const string Id = "quality-studio/core";
    public const string Version = "1.0.0";
    public const int SchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    private const string ResourceSuffix = "catalogues.quality-studio-core.v1.json";

    public static QualityTaxonomyCatalogue LoadCoreCatalogue()
    {
        using var stream = typeof(QualityTaxonomy).Assembly.GetManifestResourceStream(
            typeof(QualityTaxonomy).Assembly.GetManifestResourceNames().Single(name =>
                name.EndsWith(ResourceSuffix, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("The core quality taxonomy resource is missing.");
        return JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(stream, QualityObservationJson.Options)
            ?? throw new JsonException("The core quality taxonomy must be a JSON object.");
    }
}

public sealed record QualityTaxonomyCatalogue(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string Id,
    [property: JsonPropertyOrder(3)] string Version,
    [property: JsonPropertyOrder(4)] IReadOnlyList<QualityTaxonomyAxis> Axes,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityAspectTerm> Aspects,
    [property: JsonPropertyOrder(6)] IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyAxis(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated = false,
    string? ReplacedBy = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> AllowedAssessmentAxes,
    bool Deprecated = false,
    string? ReplacedBy = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record TaxonomyReference(
    string Id,
    string Version,
    string Digest,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationSubject(
    string UnitId,
    string ManifestHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationProducer(
    string Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    string ReviewRunId,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record EvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    string? ArtifactRef = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationEvidence(
    string Id,
    string Kind,
    EvidenceLocator? Locator,
    string Summary,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Raw = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationGrade(
    int Score,
    string Band,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationAspect(
    string AspectId,
    string Assessment,
    string Rationale,
    ObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationFindingSource(
    string Kind,
    string ProducerRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationFinding(
    string ObservationFindingId,
    string? IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    string Severity,
    IReadOnlyList<string> EvidenceRefs,
    ObservationFindingSource Source,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record ObservationDecision(
    string Value,
    string PolicyRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservation
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)] public required string ObservationId { get; init; }
    [JsonPropertyOrder(3)] public required TaxonomyReference Taxonomy { get; init; }
    [JsonPropertyOrder(4)] public IReadOnlyList<TaxonomyReference> ExtensionTaxonomies { get; init; } = [];
    [JsonPropertyOrder(5)] public required ObservationSubject Subject { get; init; }
    [JsonPropertyOrder(6)] public required ObservationProfile Profile { get; init; }
    [JsonPropertyOrder(7)] public required ObservationProducer Producer { get; init; }
    [JsonPropertyOrder(8)] public required string EvidenceStatus { get; init; }
    [JsonPropertyOrder(9)] public IReadOnlyList<ObservationEvidence> Evidence { get; init; } = [];
    [JsonPropertyOrder(10)] public IReadOnlyList<ObservationAspect> Aspects { get; init; } = [];
    [JsonPropertyOrder(11)] public required string Assessment { get; init; }
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObservationDecision? Decision { get; init; }
    [JsonPropertyOrder(13)] public IReadOnlyList<ObservationFinding> Findings { get; init; } = [];
    [JsonPropertyOrder(14)] public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>();

    // Legacy review-meta root x-* fields remain losslessly readable during migration.
    [JsonExtensionData, JsonPropertyOrder(15)]
    public IDictionary<string, JsonElement> LegacyExtensions { get; init; } =
        new Dictionary<string, JsonElement>();
}

public sealed record QualityObservationReadResult(
    JsonElement Raw,
    QualityObservation? Observation,
    string? UnsupportedReason)
{
    public bool IsSupported => Observation is not null;
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static QualityObservationReadResult Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A quality observation must be a JSON object.");
        }

        var version = raw.TryGetProperty("schemaVersion", out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
        if (version != QualityObservation.CurrentSchemaVersion)
        {
            return new(raw, null, $"Unsupported quality observation schemaVersion '{version}'.");
        }

        if (!raw.TryGetProperty("taxonomy", out var taxonomy) ||
            !taxonomy.TryGetProperty("id", out var taxonomyId) ||
            !taxonomy.TryGetProperty("version", out var taxonomyVersion) ||
            !string.Equals(taxonomyId.GetString(), QualityTaxonomy.Id, StringComparison.Ordinal) ||
            taxonomyVersion.GetString() is not { } versionText ||
            !versionText.StartsWith("1.", StringComparison.Ordinal))
        {
            return new(raw, null, "Unsupported core taxonomy major.");
        }

        var observation = JsonSerializer.Deserialize<QualityObservation>(raw, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        Validate(observation);
        return new(raw, observation, null);
    }

    public static QualityObservation Deserialize(string json)
    {
        var result = Read(json);
        return result.Observation ?? throw new JsonException(result.UnsupportedReason);
    }

    public static string Serialize(QualityObservation observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static string ComputeDigest(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void Validate(QualityObservation observation)
    {
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservation.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        }

        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomy.Id, StringComparison.Ordinal) ||
            !observation.Taxonomy.Version.StartsWith("1.", StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported core taxonomy '{observation.Taxonomy.Id}@{observation.Taxonomy.Version}'.");
        }

        foreach (var key in observation.LegacyExtensions.Keys)
        {
            if (!key.StartsWith("x-", StringComparison.Ordinal))
            {
                throw new JsonException($"Unknown root property '{key}' is not a legacy x-* extension.");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        return new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
    }
}

public sealed class QualityTaxonomyResolver
{
    private readonly HashSet<string> recognizedAspects;

    public QualityTaxonomyResolver(
        QualityTaxonomyCatalogue core,
        IEnumerable<QualityTaxonomyCatalogue>? installedExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(core);
        recognizedAspects = core.Aspects.Select(aspect => aspect.Id).ToHashSet(StringComparer.Ordinal);
        if (installedExtensions is null)
        {
            return;
        }

        foreach (var extension in installedExtensions)
        {
            recognizedAspects.UnionWith(extension.Aspects.Select(aspect => aspect.Id));
        }
    }

    public bool IsAspectRecognized(string aspectId) => recognizedAspects.Contains(aspectId);

    public IReadOnlyList<ObservationAspect> SelectAggregatableAspects(QualityObservation observation) =>
        observation.Aspects.Where(aspect => IsAspectRecognized(aspect.AspectId)).ToArray();
}

public sealed record LegacyProjection(
    string Axis,
    string Value,
    string? EvidenceStatus = null,
    string? Decision = null,
    string? PolicyRef = null);

public static class LegacyQualityMapping
{
    public const string SecurityPolicy = "security-sensor-agent-v1";

    public static LegacyProjection SecurityVerdict(string value) => value switch
    {
        "pass" => new("assessment", "pass", "available", "allow", SecurityPolicy),
        "warn" => new("assessment", "concern", PolicyRef: SecurityPolicy),
        "block" => new("assessment", "fail", Decision: "block", PolicyRef: SecurityPolicy),
        "unavailable" => new("assessment", "inconclusive", "unavailable", PolicyRef: SecurityPolicy),
        _ => Unknown("security verdict", value),
    };

    public static LegacyProjection FlowVerdict(string value) => new("assessment", value switch
    {
        "pass" => "pass",
        "fail" => "fail",
        "undetermined" => "inconclusive",
        _ => throw UnknownException("flow verdict", value),
    });

    public static LegacyProjection AttackVerdict(string value) => new("assessment", value switch
    {
        "pass" => "pass",
        "finding" => "fail",
        "not-applicable" => "not-applicable",
        "not-yet-checked" => "not-assessed",
        _ => throw UnknownException("attack verdict", value),
    });

    public static LegacyProjection ChangeSummary(string value) => new("change", value switch
    {
        "no-quality-delta" => "no-observed-delta",
        "improved" => "improved",
        "neutral" => "unchanged",
        "regression" => "regressed",
        _ => throw UnknownException("change summary", value),
    });

    public static LegacyProjection ChangeAspect(string value) => new("assessment", value switch
    {
        "good" => "pass",
        "mixed" => "concern",
        "concerning" => "fail",
        "unknown" => "inconclusive",
        _ => throw UnknownException("change aspect", value),
    });

    public static LegacyProjection FindingState(string value) => new("lifecycle", value switch
    {
        "accepted" => "accepted-risk",
        "falsePositive" or "false-positive" => "false-positive",
        "open" or "waived" or "resolved" => value,
        _ => throw UnknownException("finding state", value),
    });

    public static ObservationEvidence Evidence(string value)
    {
        var hash = QualityObservationJson.ComputeDigest(value);
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return new("legacy-evidence", "tool-result", null, "Legacy JSON evidence.", hash,
                "application/json", parsed.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new("legacy-evidence", "document", null, value, hash, "text/plain",
                JsonSerializer.SerializeToElement(value));
        }
    }

    private static LegacyProjection Unknown(string kind, string value) => throw UnknownException(kind, value);

    private static ArgumentOutOfRangeException UnknownException(string kind, string value) =>
        new(nameof(value), value, $"Unknown legacy {kind}.");
}
