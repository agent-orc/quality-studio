using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyCatalogue(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    string Prefix,
    IReadOnlyDictionary<string, IReadOnlyList<QualityTaxonomyTerm>> Axes,
    IReadOnlyList<QualityAspectTerm> Aspects,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyTerm(
    string Id,
    int Order,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> AllowedAxes,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityCatalogueReference(
    string Id,
    string Version,
    string Digest,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public static class QualityTaxonomy
{
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const int SchemaVersion = 1;
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";

    private static readonly Lazy<(QualityTaxonomyCatalogue Catalogue, string Digest)> Core = new(LoadCore);

    public static QualityTaxonomyCatalogue CoreCatalogue => Core.Value.Catalogue;

    public static QualityCatalogueReference CoreReference =>
        new(CoreId, CoreVersion, Core.Value.Digest);

    public static bool IsCoreTerm(string axis, string value) =>
        CoreCatalogue.Axes.TryGetValue(axis, out var terms) &&
        terms.Any(term => string.Equals(term.Id, value, StringComparison.Ordinal));

    public static string? ResolveCoreAspect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var match = CoreCatalogue.Aspects.FirstOrDefault(aspect =>
            string.Equals(aspect.Id, value, StringComparison.Ordinal) ||
            (aspect.Aliases?.Contains(value, StringComparer.Ordinal) ?? false));
        return match?.Id;
    }

    private static (QualityTaxonomyCatalogue, string) LoadCore()
    {
        var assembly = typeof(QualityTaxonomy).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("quality-taxonomy.core.v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded core quality taxonomy was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(bytes, QualityObservationJson.Options)
            ?? throw new InvalidDataException("The embedded core quality taxonomy is empty.");
        if (catalogue.SchemaVersion != SchemaVersion ||
            !string.Equals(catalogue.Schema, SchemaId, StringComparison.Ordinal) ||
            !string.Equals(catalogue.Id, CoreId, StringComparison.Ordinal) ||
            !string.Equals(catalogue.Version, CoreVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The embedded core quality taxonomy has an unsupported identity.");
        }

        return (catalogue, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

public static class QualityTerms
{
    public static class ProducerKind
    {
        public const string Agent = "agent";
        public const string DeterministicSensor = "deterministic-sensor";
        public const string Human = "human";
        public const string Imported = "imported";
        public const string Unknown = "unknown";
    }

    public static class EvidenceStatus
    {
        public const string Available = "available";
        public const string Partial = "partial";
        public const string Unavailable = "unavailable";
    }

    public static class Assessment
    {
        public const string Pass = "pass";
        public const string Concern = "concern";
        public const string Fail = "fail";
        public const string Inconclusive = "inconclusive";
        public const string NotApplicable = "not-applicable";
        public const string NotAssessed = "not-assessed";
    }

    public static class Change
    {
        public const string Improved = "improved";
        public const string Regressed = "regressed";
        public const string Mixed = "mixed";
        public const string Unchanged = "unchanged";
        public const string NoObservedDelta = "no-observed-delta";
        public const string Inconclusive = "inconclusive";
    }

    public static class Decision
    {
        public const string Allow = "allow";
        public const string Warn = "warn";
        public const string Block = "block";
        public const string Defer = "defer";
    }

    public static class Lifecycle
    {
        public const string Open = "open";
        public const string AcceptedRisk = "accepted-risk";
        public const string Waived = "waived";
        public const string FalsePositive = "false-positive";
        public const string Resolved = "resolved";
    }

    public static class EvidenceKind
    {
        public const string SourceCode = "source-code";
        public const string TestResult = "test-result";
        public const string RuntimeMeasurement = "runtime-measurement";
        public const string ToolResult = "tool-result";
        public const string Artifact = "artifact";
        public const string Document = "document";
        public const string HumanAttestation = "human-attestation";
    }
}

public sealed record LegacyQualityProjection(
    string? Assessment = null,
    string? EvidenceStatus = null,
    string? Change = null,
    string? Decision = null,
    string? PolicyRef = null);

/// <summary>Pure, deterministic adapters from legacy protocol values into core v1 axes.</summary>
public static class LegacyQualityMapping
{
    public static LegacyQualityProjection SecurityVerdict(string value) => value switch
    {
        "pass" => new(QualityTerms.Assessment.Pass, QualityTerms.EvidenceStatus.Available,
            Decision: QualityTerms.Decision.Allow, PolicyRef: "security-sensor-agent-v1"),
        "warn" => new(QualityTerms.Assessment.Concern, QualityTerms.EvidenceStatus.Available,
            Decision: QualityTerms.Decision.Warn, PolicyRef: "security-sensor-agent-v1"),
        "block" => new(QualityTerms.Assessment.Fail, QualityTerms.EvidenceStatus.Available,
            Decision: QualityTerms.Decision.Block, PolicyRef: "security-sensor-agent-v1"),
        "unavailable" => new(QualityTerms.Assessment.Inconclusive, QualityTerms.EvidenceStatus.Unavailable,
            Decision: QualityTerms.Decision.Defer, PolicyRef: "security-sensor-agent-v1"),
        _ => throw Unknown("security verdict", value),
    };

    public static LegacyQualityProjection FlowVerdict(string value) => value switch
    {
        "pass" => new(QualityTerms.Assessment.Pass),
        "fail" => new(QualityTerms.Assessment.Fail),
        "undetermined" => new(QualityTerms.Assessment.Inconclusive),
        _ => throw Unknown("flow verdict", value),
    };

    public static LegacyQualityProjection AttackVerdict(string value) => value switch
    {
        "pass" => new(QualityTerms.Assessment.Pass),
        "finding" => new(QualityTerms.Assessment.Fail),
        "not-applicable" => new(QualityTerms.Assessment.NotApplicable),
        "not-yet-checked" => new(QualityTerms.Assessment.NotAssessed),
        _ => throw Unknown("attack verdict", value),
    };

    public static LegacyQualityProjection ChangeSummary(string value) => value switch
    {
        "no-quality-delta" => new(Change: QualityTerms.Change.NoObservedDelta),
        "improved" => new(Change: QualityTerms.Change.Improved),
        "neutral" => new(Change: QualityTerms.Change.Unchanged),
        "regression" => new(Change: QualityTerms.Change.Regressed),
        _ => throw Unknown("change summary", value),
    };

    public static LegacyQualityProjection ChangeAspect(string value) => value switch
    {
        "good" => new(QualityTerms.Assessment.Pass),
        "mixed" => new(QualityTerms.Assessment.Concern),
        "concerning" => new(QualityTerms.Assessment.Fail),
        "unknown" => new(QualityTerms.Assessment.Inconclusive),
        _ => throw Unknown("change aspect", value),
    };

    public static string FindingState(string value) => value switch
    {
        "open" => QualityTerms.Lifecycle.Open,
        "accepted" or "accepted-risk" => QualityTerms.Lifecycle.AcceptedRisk,
        "waived" => QualityTerms.Lifecycle.Waived,
        "falsePositive" or "false-positive" => QualityTerms.Lifecycle.FalsePositive,
        "resolved" => QualityTerms.Lifecycle.Resolved,
        _ => throw Unknown("finding state", value),
    };

    public static string? Aspect(string value) => QualityTaxonomy.ResolveCoreAspect(value);

    public static QualityEvidence Evidence(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            using var document = JsonDocument.Parse(value);
            return new QualityEvidence(id, QualityTerms.EvidenceKind.ToolResult,
                "Legacy structured evidence.",
                ContentHash: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                MediaType: "application/json",
                Raw: document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new QualityEvidence(id, QualityTerms.EvidenceKind.ToolResult,
                string.IsNullOrWhiteSpace(value) ? "Legacy textual evidence." : value,
                ContentHash: "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                MediaType: "text/plain",
                Raw: JsonSerializer.SerializeToElement(value));
        }
    }

    private static ArgumentOutOfRangeException Unknown(string axis, string value) =>
        new(nameof(value), value, $"Unknown legacy {axis} value '{value}'.");
}
