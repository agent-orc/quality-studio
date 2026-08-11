using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public abstract record QualityExtensibleObject
{
    [JsonPropertyOrder(90), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Extensions { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

public sealed record QualityTaxonomyTerm(
    string Axis,
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    string? Replacement = null) : QualityExtensibleObject;

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> AllowedAxes,
    bool Deprecated,
    string? Replacement = null) : QualityExtensibleObject;

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string Id,
    [property: JsonPropertyOrder(3)] string Version,
    [property: JsonPropertyOrder(4)] IReadOnlyList<QualityTaxonomyTerm> Terms,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityAspectTerm> Aspects) : QualityExtensibleObject
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
}

public static class CoreQualityTerms
{
    public static class Axes
    {
        public const string ProducerKind = "producer-kind";
        public const string EvidenceStatus = "evidence-status";
        public const string Assessment = "assessment";
        public const string Change = "change";
        public const string Decision = "decision";
        public const string Severity = "severity";
        public const string Lifecycle = "lifecycle";
        public const string EvidenceKind = "evidence-kind";
    }

    public static class ProducerKinds
    {
        public const string Agent = "agent";
        public const string DeterministicSensor = "deterministic-sensor";
        public const string Human = "human";
        public const string Imported = "imported";
        public const string Unknown = "unknown";
    }

    public static class EvidenceStatuses
    {
        public const string Available = "available";
        public const string Partial = "partial";
        public const string Unavailable = "unavailable";
    }

    public static class Assessments
    {
        public const string Pass = "pass";
        public const string Concern = "concern";
        public const string Fail = "fail";
        public const string Inconclusive = "inconclusive";
        public const string NotApplicable = "not-applicable";
        public const string NotAssessed = "not-assessed";
    }

    public static class Changes
    {
        public const string Improved = "improved";
        public const string Regressed = "regressed";
        public const string Mixed = "mixed";
        public const string Unchanged = "unchanged";
        public const string NoObservedDelta = "no-observed-delta";
        public const string Inconclusive = "inconclusive";
    }

    public static class Decisions
    {
        public const string Allow = "allow";
        public const string Warn = "warn";
        public const string Block = "block";
        public const string Defer = "defer";
    }

    public static class Lifecycles
    {
        public const string Open = "open";
        public const string AcceptedRisk = "accepted-risk";
        public const string Waived = "waived";
        public const string FalsePositive = "false-positive";
        public const string Resolved = "resolved";
    }

    public static class EvidenceKinds
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

public sealed class CoreQualityCatalogue
{
    public const string CatalogueId = "quality-studio/core";
    public const string CatalogueVersion = "1.0.0";
    private const string ResourceSuffix = "catalogues.quality-taxonomy.core.v1.json";
    private static readonly Lazy<CoreQualityCatalogue> BuiltIn = new(LoadBuiltIn);
    private readonly IReadOnlyDictionary<(string Axis, string Value), QualityTaxonomyTerm> _terms;
    private readonly IReadOnlyDictionary<string, QualityAspectTerm> _aspects;

    private CoreQualityCatalogue(QualityTaxonomyDocument document, string digest)
    {
        Document = document;
        Digest = digest;
        _terms = BuildTermIndex(document);
        _aspects = BuildAspectIndex(document);
    }

    public static CoreQualityCatalogue Instance => BuiltIn.Value;

    public QualityTaxonomyDocument Document { get; }

    public string Digest { get; }

    public QualityTaxonomyReference Reference => new(CatalogueId, CatalogueVersion, Digest);

    public bool TryResolveTerm(string axis, string value, out QualityTaxonomyTerm? term) =>
        _terms.TryGetValue((axis, value), out term);

    public bool TryResolveAspect(string value, out QualityAspectTerm? aspect) =>
        _aspects.TryGetValue(value, out aspect);

    public bool SupportsAspect(string value, string axis) =>
        TryResolveAspect(value, out var aspect) &&
        aspect!.AllowedAxes.Contains(axis, StringComparer.Ordinal);

    private static CoreQualityCatalogue LoadBuiltIn()
    {
        var assembly = typeof(CoreQualityCatalogue).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded taxonomy catalogue '{resourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var document = JsonSerializer.Deserialize<QualityTaxonomyDocument>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The embedded taxonomy catalogue must be a JSON object.");
        Validate(document);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new CoreQualityCatalogue(document, digest);
    }

    private static void Validate(QualityTaxonomyDocument document)
    {
        if (document.SchemaVersion != QualityTaxonomyDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityTaxonomyDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported taxonomy schemaVersion '{document.SchemaVersion}'.");
        if (!string.Equals(document.Id, CatalogueId, StringComparison.Ordinal) ||
            !string.Equals(document.Version, CatalogueVersion, StringComparison.Ordinal))
            throw new JsonException("The embedded core taxonomy has an unexpected identity or version.");
        if (document.Terms.Count == 0 || document.Aspects.Count == 0)
            throw new JsonException("The core taxonomy must define terms and aspects.");

        foreach (var grouping in document.Terms.GroupBy(term => term.Axis, StringComparer.Ordinal))
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var orders = new HashSet<int>();
            foreach (var term in grouping)
            {
                if (!ids.Add(term.Id) || term.Aliases.Any(alias => !ids.Add(alias)))
                    throw new JsonException($"Taxonomy axis '{grouping.Key}' contains a duplicate id or alias.");
                if (!orders.Add(term.Order))
                    throw new JsonException($"Taxonomy axis '{grouping.Key}' contains a duplicate order.");
                if (term.Deprecated && string.IsNullOrWhiteSpace(term.Replacement))
                    throw new JsonException($"Deprecated term '{term.Id}' must name a replacement.");
            }
        }

        var aspectIds = new HashSet<string>(StringComparer.Ordinal);
        var aspectOrders = new HashSet<int>();
        foreach (var aspect in document.Aspects)
        {
            if (!aspectIds.Add(aspect.Id) || aspect.Aliases.Any(alias => !aspectIds.Add(alias)))
                throw new JsonException("The taxonomy contains a duplicate aspect id or alias.");
            if (!aspectOrders.Add(aspect.Order))
                throw new JsonException("The taxonomy contains a duplicate aspect order.");
            if (aspect.AllowedAxes.Count == 0)
                throw new JsonException($"Aspect '{aspect.Id}' must allow at least one axis.");
            if (aspect.Deprecated && string.IsNullOrWhiteSpace(aspect.Replacement))
                throw new JsonException($"Deprecated aspect '{aspect.Id}' must name a replacement.");
        }
    }

    private static IReadOnlyDictionary<(string Axis, string Value), QualityTaxonomyTerm> BuildTermIndex(
        QualityTaxonomyDocument document)
    {
        var result = new Dictionary<(string Axis, string Value), QualityTaxonomyTerm>();
        foreach (var term in document.Terms)
        {
            result.Add((term.Axis, term.Id), term);
            foreach (var alias in term.Aliases) result.Add((term.Axis, alias), term);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, QualityAspectTerm> BuildAspectIndex(QualityTaxonomyDocument document)
    {
        var result = new Dictionary<string, QualityAspectTerm>(StringComparer.Ordinal);
        foreach (var aspect in document.Aspects)
        {
            result.Add(aspect.Id, aspect);
            foreach (var alias in aspect.Aliases) result.Add(alias, aspect);
        }
        return result;
    }
}
