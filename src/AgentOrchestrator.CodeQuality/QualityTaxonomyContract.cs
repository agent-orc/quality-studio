using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Canonical producer kind. Always explicit; absence never means agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityProducerKind>))]
public enum QualityProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

/// <summary>Describes evidence coverage only. Never a pass or block by itself.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceStatus>))]
public enum QualityEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

/// <summary>What the evidence establishes for a review or aspect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityAssessment>))]
public enum QualityAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

/// <summary>Direction of a before/after comparison. Never used outside a comparison.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityChange>))]
public enum QualityChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

/// <summary>An optional policy output. Never supplied directly by a raw finding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityDecision>))]
public enum QualityDecision
{
    Allow,
    Warn,
    Block,
    Defer,
}

/// <summary>Lives in lifecycle events, not observations. An observation cannot set these directly.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityLifecycle>))]
public enum QualityLifecycle
{
    Open,
    AcceptedRisk,
    Waived,
    FalsePositive,
    Resolved,
}

/// <summary>Typed evidence kind. Every evidence item has a locator or artifact reference and a summary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceKind>))]
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

public sealed record QualityTaxonomyTerm(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
}

public sealed record QualityTaxonomyAxis(
    string Id,
    string Title,
    IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
}

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects);

/// <summary>Pins one taxonomy catalogue by reverse-DNS id, SemVer, and SHA-256 digest.</summary>
public sealed record QualityTaxonomyReference(string Id, string Version, string Digest);

/// <summary>How a term or aspect id relates to the loaded core catalogue.</summary>
public enum QualityTermResolution
{
    /// <summary>The id (or one of its aliases) is a core term of the installed catalogue.</summary>
    Core,

    /// <summary>The taxonomy major differs from the loaded catalogue. The raw value is preserved but not interpreted.</summary>
    QuarantinedMajor,

    /// <summary>The taxonomy major matches, but the id is not a known core term. It round-trips but is excluded from core aggregates.</summary>
    UnrecognizedTerm,
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Hash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

/// <summary>
/// Loads and resolves the embedded core taxonomy catalogue. The catalogue pins exact
/// terms, aliases, and deprecations; it never infers a meaning from an absent field.
/// </summary>
public sealed class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy.core.v1.json";
    private readonly Dictionary<string, QualityTaxonomyAspect> aspectsById;
    private readonly Dictionary<string, QualityTaxonomyAspect> aspectsByAlias;
    private readonly Dictionary<string, QualityTaxonomyAxis> axesById;

    public QualityTaxonomyCatalogueResolver()
        : this(ReadBuiltIn())
    {
    }

    public QualityTaxonomyCatalogueResolver(QualityTaxonomyCatalogueDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);
        Document = document;
        Reference = new QualityTaxonomyReference(document.Id, document.Version, QualityTaxonomyJson.Hash(document));
        aspectsById = document.Aspects.ToDictionary(aspect => aspect.Id, StringComparer.Ordinal);
        aspectsByAlias = document.Aspects
            .SelectMany(aspect => aspect.Aliases.Select(alias => (alias, aspect)))
            .ToDictionary(pair => pair.alias, pair => pair.aspect, StringComparer.Ordinal);
        axesById = document.Axes.ToDictionary(axis => axis.Id, StringComparer.Ordinal);
    }

    public QualityTaxonomyCatalogueDocument Document { get; }

    public QualityTaxonomyReference Reference { get; }

    public QualityTaxonomyAspect? TryResolveAspect(string idOrAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrAlias);
        if (aspectsById.TryGetValue(idOrAlias, out var byId)) return byId;
        return aspectsByAlias.GetValueOrDefault(idOrAlias);
    }

    public QualityTaxonomyTerm? TryResolveAxisTerm(string axisId, string idOrAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrAlias);
        if (!axesById.TryGetValue(axisId, out var axis)) return null;
        foreach (var term in axis.Terms)
        {
            if (string.Equals(term.Id, idOrAlias, StringComparison.Ordinal)) return term;
            if (term.Aliases.Contains(idOrAlias, StringComparer.Ordinal)) return term;
        }
        return null;
    }

    /// <summary>
    /// Classifies an aspect id carried by an observation against the taxonomy it declares.
    /// An unknown major is quarantined without discarding the raw value; a same-major id
    /// outside the catalogue remains visible but is excluded from core aggregation.
    /// </summary>
    public QualityTermResolution ClassifyAspect(QualityTaxonomyReference declaredTaxonomy, string aspectId)
    {
        ArgumentNullException.ThrowIfNull(declaredTaxonomy);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspectId);
        if (!string.Equals(declaredTaxonomy.Id, Reference.Id, StringComparison.Ordinal) ||
            Major(declaredTaxonomy.Version) != Major(Reference.Version))
        {
            return QualityTermResolution.QuarantinedMajor;
        }

        return TryResolveAspect(aspectId) is not null
            ? QualityTermResolution.Core
            : QualityTermResolution.UnrecognizedTerm;
    }

    private static int Major(string semVer) =>
        int.Parse(semVer.Split('.', 2)[0]);

    private static QualityTaxonomyCatalogueDocument ReadBuiltIn()
    {
        var assembly = typeof(QualityTaxonomyCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        return JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(stream, QualityTaxonomyJson.Options)
            ?? throw new JsonException("The built-in quality taxonomy catalogue is empty.");
    }

    private static void Validate(QualityTaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.Version) || document.Axes is not { Count: > 0 } ||
            document.Aspects is null)
            throw new JsonException("Quality taxonomy catalogue has an unsupported contract.");
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Quality taxonomy catalogue contains duplicate aspect ids.");
        var allAliases = document.Aspects.SelectMany(aspect => aspect.Aliases).ToArray();
        if (allAliases.Length != allAliases.Distinct(StringComparer.Ordinal).Count())
            throw new JsonException("Quality taxonomy catalogue contains duplicate aspect aliases.");
        foreach (var axis in document.Axes)
        {
            if (axis.Terms.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException($"Quality taxonomy axis '{axis.Id}' contains duplicate term ids.");
        }
    }
}
