using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

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

public sealed record QualityTaxonomyIdentity(string Id, string Version);

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
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    string? Description = null);

public sealed record QualityAspectDefinition(
    string Id,
    string Title,
    string? Description = null,
    string? MigrationAlias = null,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null)
{
    public IReadOnlyList<string> Aliases { get; init; } = Aliases ?? [];
}

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    QualityTaxonomyIdentity Taxonomy,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityAspectDefinition> Aspects);

public sealed record ResolvedQualityTaxonomy(
    QualityTaxonomyCatalogueDocument Document,
    string Digest)
{
    public string Id => Document.Taxonomy.Id;
    public string Version => Document.Taxonomy.Version;

    public QualityTaxonomyAxis? FindAxis(string axisId) =>
        Document.Axes.FirstOrDefault(axis => axis.Id.Equals(axisId, StringComparison.Ordinal));

    public QualityAspectDefinition? FindAspect(string aspectId) =>
        Document.Aspects.FirstOrDefault(aspect => aspect.Id.Equals(aspectId, StringComparison.Ordinal));
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
/// Loads the built-in core taxonomy catalogue. T1 has no project/global override layer;
/// the extension contract (reverse-DNS extension catalogues) is a T2+ writer concern.
/// </summary>
public sealed class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public ResolvedQualityTaxonomy ResolveBuiltIn()
    {
        var document = ReadBuiltIn();
        Validate(document, "embedded:" + BuiltInResourceSuffix);
        return new ResolvedQualityTaxonomy(document, QualityTaxonomyJson.Hash(document));
    }

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

    private static void Validate(QualityTaxonomyCatalogueDocument document, string source)
    {
        if (document.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(document.Taxonomy?.Id) ||
            string.IsNullOrWhiteSpace(document.Taxonomy?.Version) ||
            document.Axes is not { Count: > 0 } ||
            document.Aspects is not { Count: > 0 })
            throw new JsonException($"Quality taxonomy catalogue '{source}' has an unsupported contract.");

        if (document.Axes.GroupBy(axis => axis.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate axis ids.");

        foreach (var axis in document.Axes)
        {
            if (axis.Terms is not { Count: > 0 })
                throw new JsonException($"Quality taxonomy catalogue '{source}' axis '{axis.Id}' has no terms.");
            if (axis.Terms.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException($"Quality taxonomy catalogue '{source}' axis '{axis.Id}' has duplicate term ids.");
        }

        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate aspect ids.");
    }
}
