using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public static class QualityTaxonomyTerms
{
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
    public const int SupportedCoreMajor = 1;

    public static readonly IReadOnlySet<string> ProducerKinds = new HashSet<string>(
        ["agent", "deterministic-sensor", "human", "imported", "unknown"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> EvidenceStatuses = new HashSet<string>(
        ["available", "partial", "unavailable"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Assessments = new HashSet<string>(
        ["pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Changes = new HashSet<string>(
        ["improved", "regressed", "mixed", "unchanged", "no-observed-delta", "inconclusive"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Decisions = new HashSet<string>(
        ["allow", "warn", "block", "defer"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Severities = new HashSet<string>(
        ["critical", "high", "medium", "low", "info"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> Lifecycles = new HashSet<string>(
        ["open", "accepted-risk", "waived", "false-positive", "resolved"], StringComparer.Ordinal);

    public static readonly IReadOnlySet<string> EvidenceKinds = new HashSet<string>(
        ["source-code", "test-result", "runtime-measurement", "tool-result", "artifact", "document", "human-attestation"],
        StringComparer.Ordinal);
}

public sealed record QualityTaxonomyCatalogue
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string Id { get; init; }

    [JsonPropertyOrder(3)]
    public required string Version { get; init; }

    [JsonPropertyOrder(4)]
    public required IReadOnlyList<QualityTaxonomyAxis> Axes { get; init; }

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<QualityAspectTerm> Aspects { get; init; }

    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record QualityTaxonomyAxis(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    int Order,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Replacement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> AllowedAxes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Replacement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public static class QualityTaxonomyCatalogueStore
{
    private const string CoreResource =
        "AgentOrchestrator.CodeQuality.catalogues.quality-studio-core.v1.json";
    private static readonly Lazy<(QualityTaxonomyCatalogue Catalogue, string Digest)> Core = new(LoadCore);

    public static QualityTaxonomyCatalogue CoreCatalogue => Core.Value.Catalogue;

    public static string CoreDigest => Core.Value.Digest;

    private static (QualityTaxonomyCatalogue, string) LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CoreResource)
            ?? throw new InvalidOperationException($"Embedded taxonomy catalogue '{CoreResource}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The embedded core taxonomy catalogue must be a JSON object.");
        if (catalogue.SchemaVersion != QualityTaxonomyCatalogue.CurrentSchemaVersion ||
            !string.Equals(catalogue.Schema, QualityTaxonomyCatalogue.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(catalogue.Id, QualityTaxonomyTerms.CoreId, StringComparison.Ordinal) ||
            !string.Equals(catalogue.Version, QualityTaxonomyTerms.CoreVersion, StringComparison.Ordinal))
        {
            throw new JsonException("The embedded core taxonomy catalogue identity is invalid.");
        }

        return (catalogue, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

public enum LegacyQualityContract
{
    SecurityVerdict,
    FlowVerdict,
    AttackVerdict,
    ChangeSummary,
    ChangeAspect,
    FindingState,
}

public sealed record LegacyTermMapping(
    string Axis,
    string Value,
    string? EvidenceStatus = null,
    string? Decision = null,
    string? PolicyRef = null);

public static class LegacyQualityTaxonomyMapper
{
    private static readonly IReadOnlyDictionary<(LegacyQualityContract Contract, string Value), LegacyTermMapping> Terms =
        new Dictionary<(LegacyQualityContract, string), LegacyTermMapping>
        {
            [(LegacyQualityContract.SecurityVerdict, "pass")] =
                new("assessment", "pass", "available"),
            [(LegacyQualityContract.SecurityVerdict, "warn")] =
                new("assessment", "concern", PolicyRef: "security-sensor-agent-v1"),
            [(LegacyQualityContract.SecurityVerdict, "block")] =
                new("assessment", "fail", Decision: "block", PolicyRef: "security-sensor-agent-v1"),
            [(LegacyQualityContract.SecurityVerdict, "unavailable")] =
                new("assessment", "inconclusive", "unavailable", PolicyRef: "security-sensor-agent-v1"),
            [(LegacyQualityContract.FlowVerdict, "pass")] = new("assessment", "pass"),
            [(LegacyQualityContract.FlowVerdict, "fail")] = new("assessment", "fail"),
            [(LegacyQualityContract.FlowVerdict, "undetermined")] = new("assessment", "inconclusive"),
            [(LegacyQualityContract.AttackVerdict, "pass")] = new("assessment", "pass"),
            [(LegacyQualityContract.AttackVerdict, "finding")] = new("assessment", "fail"),
            [(LegacyQualityContract.AttackVerdict, "not-applicable")] = new("assessment", "not-applicable"),
            [(LegacyQualityContract.AttackVerdict, "not-yet-checked")] = new("assessment", "not-assessed"),
            [(LegacyQualityContract.ChangeSummary, "no-quality-delta")] = new("change", "no-observed-delta"),
            [(LegacyQualityContract.ChangeSummary, "improved")] = new("change", "improved"),
            [(LegacyQualityContract.ChangeSummary, "neutral")] = new("change", "unchanged"),
            [(LegacyQualityContract.ChangeSummary, "regression")] = new("change", "regressed"),
            [(LegacyQualityContract.ChangeAspect, "good")] = new("assessment", "pass"),
            [(LegacyQualityContract.ChangeAspect, "mixed")] = new("assessment", "concern"),
            [(LegacyQualityContract.ChangeAspect, "concerning")] = new("assessment", "fail"),
            [(LegacyQualityContract.ChangeAspect, "unknown")] = new("assessment", "inconclusive"),
            [(LegacyQualityContract.FindingState, "open")] = new("lifecycle", "open"),
            [(LegacyQualityContract.FindingState, "accepted")] = new("lifecycle", "accepted-risk"),
            [(LegacyQualityContract.FindingState, "accepted-risk")] = new("lifecycle", "accepted-risk"),
            [(LegacyQualityContract.FindingState, "waived")] = new("lifecycle", "waived"),
            [(LegacyQualityContract.FindingState, "falsePositive")] = new("lifecycle", "false-positive"),
            [(LegacyQualityContract.FindingState, "false-positive")] = new("lifecycle", "false-positive"),
            [(LegacyQualityContract.FindingState, "resolved")] = new("lifecycle", "resolved"),
        };

    private static readonly IReadOnlyDictionary<string, string> AspectAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["correctness"] = "code.correctness",
        ["architecture"] = "code.architecture",
        ["security"] = "security.general",
        ["secrets"] = "security.secrets",
        ["dependencies"] = "security.dependencies",
        ["authentication-authorization"] = "security.authentication-authorization",
        ["input-validation"] = "security.input-validation",
        ["configuration-iac"] = "security.configuration-iac",
        ["boundaries"] = "security.boundary-exposure",
        ["performance"] = "performance.general",
        ["risk"] = "change.risk",
        ["test-evidence"] = "change.test-evidence",
        ["scope-discipline"] = "change.scope-discipline",
        ["architecture-drift"] = "change.architecture-drift",
    };

    public static LegacyTermMapping Map(LegacyQualityContract contract, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Terms.TryGetValue((contract, value), out var mapping)
            ? mapping
            : throw new ArgumentException($"Unsupported {contract} value '{value}'.", nameof(value));
    }

    public static bool TryMap(LegacyQualityContract contract, string value, out LegacyTermMapping? mapping) =>
        Terms.TryGetValue((contract, value), out mapping);

    public static string? MapAspect(string legacyAspect, string? deterministicRuleId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyAspect);
        if (AspectAliases.TryGetValue(legacyAspect, out var mapped)) return mapped;
        if (legacyAspect.Contains(':', StringComparison.Ordinal) ||
            QualityTaxonomyCatalogueStore.CoreCatalogue.Aspects.Any(aspect =>
                string.Equals(aspect.Id, legacyAspect, StringComparison.Ordinal)))
            return legacyAspect;
        if (!string.Equals(legacyAspect, "analyzer", StringComparison.Ordinal)) return null;
        if (!string.IsNullOrWhiteSpace(deterministicRuleId))
        {
            if (deterministicRuleId.StartsWith("dependency/", StringComparison.OrdinalIgnoreCase))
                return "security.dependencies";
            if (deterministicRuleId.StartsWith("boundary/", StringComparison.OrdinalIgnoreCase))
                return "security.boundary-exposure";
        }

        return "quality.studio:sensor.analyzer";
    }

    public static QualityEvidence MapEvidence(string id, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(evidence);
        var bytes = Encoding.UTF8.GetBytes(evidence);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            using var parsed = JsonDocument.Parse(evidence);
            return new QualityEvidence(id, "tool-result", "Preserved legacy JSON evidence.",
                ContentHash: digest, ContentType: "application/json", Payload: parsed.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new QualityEvidence(id, "tool-result", "Preserved legacy text evidence.",
                ContentHash: digest, ContentType: "text/plain", Payload: JsonSerializer.SerializeToElement(evidence));
        }
    }
}
