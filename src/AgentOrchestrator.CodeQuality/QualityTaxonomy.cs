using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyProducerKind>))]
public enum TaxonomyProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyEvidenceStatus>))]
public enum TaxonomyEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyAssessment>))]
public enum TaxonomyAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyChange>))]
public enum TaxonomyChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyDecision>))]
public enum TaxonomyDecision
{
    Allow,
    Warn,
    Block,
    Defer,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyLifecycle>))]
public enum TaxonomyLifecycle
{
    Open,
    AcceptedRisk,
    Waived,
    FalsePositive,
    Resolved,
}

[JsonConverter(typeof(JsonStringEnumConverter<TaxonomyEvidenceKind>))]
public enum TaxonomyEvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

/// <summary>Pins one observation to the exact taxonomy meaning it was produced under.</summary>
public sealed record TaxonomyRef(string Id, string Version, string Digest);

public sealed record TaxonomyTermDefinition(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null);

public sealed record TaxonomyAxisDefinition(string Id, IReadOnlyList<TaxonomyTermDefinition> Terms);

public sealed record TaxonomyAspectDefinition(
    string Id,
    string Title,
    string Source,
    string? MigrationAlias = null);

public sealed record TaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<TaxonomyAxisDefinition> Axes,
    IReadOnlyList<TaxonomyAspectDefinition> Aspects);

/// <summary>
/// Loads and validates the built-in core term catalogue described in the data-model taxonomy
/// dossier (section 6). This is a term catalogue, not a JSON structure version: it changes by
/// SemVer, independently of <see cref="QualityObservation.SchemaVersion"/>.
/// </summary>
public static class QualityTaxonomyCatalogue
{
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
    public const int SupportedCoreMajor = 1;
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    private static readonly Lazy<(TaxonomyCatalogueDocument Document, string Digest)> Loaded = new(Load);

    public static TaxonomyCatalogueDocument Core => Loaded.Value.Document;

    public static string CoreDigest => Loaded.Value.Digest;

    public static TaxonomyRef CoreRef => new(CoreId, CoreVersion, CoreDigest);

    /// <summary>
    /// True when <paramref name="version"/> shares this build's supported core major version.
    /// A different major is not an error by itself: callers keep the raw observation and mark
    /// the taxonomy major as unsupported rather than discarding data.
    /// </summary>
    public static bool IsSupportedCoreVersion(string version) =>
        int.TryParse(version.AsSpan(0, version.IndexOf('.') is var dot && dot > 0 ? dot : version.Length),
            out var major) && major == SupportedCoreMajor;

    public static IReadOnlyDictionary<string, TaxonomyAspectDefinition> AspectsById { get; } =
        Core.Aspects.ToDictionary(aspect => aspect.Id, StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> AspectMigrationAliases { get; } =
        Core.Aspects
            .Where(aspect => !string.IsNullOrEmpty(aspect.MigrationAlias))
            .ToDictionary(aspect => aspect.MigrationAlias!, aspect => aspect.Id, StringComparer.Ordinal);

    private static (TaxonomyCatalogueDocument, string) Load()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy core catalogue is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var document = JsonSerializer.Deserialize<TaxonomyCatalogueDocument>(bytes, options)
            ?? throw new JsonException("The built-in quality taxonomy core catalogue is empty.");
        Validate(document);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (document, digest);
    }

    private static void Validate(TaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != 1 || !string.Equals(document.Id, CoreId, StringComparison.Ordinal) ||
            !string.Equals(document.Version, CoreVersion, StringComparison.Ordinal))
            throw new JsonException("The built-in quality taxonomy core catalogue has an unsupported contract.");
        foreach (var axis in document.Axes)
        {
            if (axis.Terms.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException($"Taxonomy axis '{axis.Id}' has duplicate term ids.");
        }
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("The quality taxonomy core catalogue has duplicate aspect ids.");
    }
}
