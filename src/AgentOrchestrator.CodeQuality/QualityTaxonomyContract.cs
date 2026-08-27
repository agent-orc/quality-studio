using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyTerm(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false);

public sealed record QualityTaxonomyAxis(
    string Id,
    string Title,
    IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    IReadOnlyList<string>? LegacyAliases = null);

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string CatalogueId,
    string CatalogueVersion,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects);

/// <summary>
/// A loaded catalogue plus its content digest, pinned onto every observation that used it
/// (see quality-observation.v1's taxonomy.digest).
/// </summary>
public sealed record ResolvedQualityTaxonomyCatalogue(
    string CatalogueId,
    string CatalogueVersion,
    string Digest,
    QualityTaxonomyCatalogueDocument Document)
{
    public bool HasTerm(string axisId, string termId) =>
        Document.Axes.Any(axis =>
            string.Equals(axis.Id, axisId, StringComparison.Ordinal) &&
            axis.Terms.Any(term => string.Equals(term.Id, termId, StringComparison.Ordinal)));

    public bool HasAspect(string aspectId) =>
        Document.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Hash(QualityTaxonomyCatalogueDocument document)
    {
        var json = JsonSerializer.Serialize(document, Options);
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
/// Loads and validates the quality-studio/core taxonomy catalogue. T1 ships the built-in
/// resource only; project/global overlays are deferred to the slice that needs them.
/// </summary>
public sealed class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public ResolvedQualityTaxonomyCatalogue ResolveBuiltIn()
    {
        var document = ReadBuiltIn();
        Validate(document, "built-in");
        return new ResolvedQualityTaxonomyCatalogue(
            document.CatalogueId, document.CatalogueVersion, QualityTaxonomyJson.Hash(document), document);
    }

    public static void Validate(QualityTaxonomyCatalogueDocument document, string source)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(document.CatalogueId) ||
            string.IsNullOrWhiteSpace(document.CatalogueVersion) ||
            document.Axes is not { Count: > 0 } ||
            document.Aspects is null)
            throw new JsonException($"Quality taxonomy catalogue '{source}' has an unsupported contract.");
        if (document.Axes.GroupBy(axis => axis.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate axis ids.");
        foreach (var axis in document.Axes)
        {
            if (axis.Terms is not { Count: > 0 })
                throw new JsonException($"Quality taxonomy catalogue '{source}' axis '{axis.Id}' has no terms.");
            if (axis.Terms.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException($"Quality taxonomy catalogue '{source}' axis '{axis.Id}' contains duplicate term ids.");
        }
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate aspect ids.");
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
}
