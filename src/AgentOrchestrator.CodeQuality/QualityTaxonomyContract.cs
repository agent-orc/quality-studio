using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false);

public sealed record QualityTaxonomyAxis(
    string Id,
    string Description,
    IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string Description,
    string? MigrationAlias = null,
    bool Deprecated = false);

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects);

/// <summary>
/// A loaded taxonomy pinned to its content digest, so an observation can prove which exact
/// term meanings it used (dossier section 6, version layers).
/// </summary>
public sealed class ResolvedQualityTaxonomy
{
    public ResolvedQualityTaxonomy(
        string id,
        string version,
        string digest,
        IReadOnlyList<QualityTaxonomyAxis> axes,
        IReadOnlyList<QualityTaxonomyAspect> aspects)
    {
        Id = id;
        Version = version;
        Digest = digest;
        Axes = axes;
        Aspects = aspects;
        AxesById = axes.ToDictionary(axis => axis.Id, StringComparer.Ordinal);
        AspectsById = aspects.ToDictionary(aspect => aspect.Id, StringComparer.Ordinal);
    }

    public string Id { get; }
    public string Version { get; }
    public string Digest { get; }
    public IReadOnlyList<QualityTaxonomyAxis> Axes { get; }
    public IReadOnlyList<QualityTaxonomyAspect> Aspects { get; }
    public IReadOnlyDictionary<string, QualityTaxonomyAxis> AxesById { get; }
    public IReadOnlyDictionary<string, QualityTaxonomyAspect> AspectsById { get; }

    /// <summary>Whether a taxonomy reference names the same catalogue and major version as this one.</summary>
    public bool IsKnownMajor(string id, string version) =>
        string.Equals(id, Id, StringComparison.Ordinal) &&
        Major(version) == Major(Version);

    public bool TryGetTerm(string axisId, string termId, out QualityTaxonomyTerm term)
    {
        if (AxesById.TryGetValue(axisId, out var axis))
        {
            var match = axis.Terms.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, termId, StringComparison.Ordinal) ||
                (candidate.Aliases?.Contains(termId, StringComparer.Ordinal) ?? false));
            if (match is not null)
            {
                term = match;
                return true;
            }
        }
        term = null!;
        return false;
    }

    public bool TryGetAspect(string aspectId, out QualityTaxonomyAspect aspect) =>
        AspectsById.TryGetValue(aspectId, out aspect!);

    private static int Major(string semanticVersion) =>
        int.Parse(semanticVersion[..semanticVersion.IndexOf('.', StringComparison.Ordinal)]);
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

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
/// Loads the built-in <c>quality-studio/core</c> taxonomy catalogue (dossier T1) and pins it to a
/// content digest. There is no per-repository override yet; that is out of T1's scope.
/// </summary>
public sealed class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public ResolvedQualityTaxonomy ResolveBuiltIn()
    {
        var document = ReadBuiltIn();
        Validate(document, "built-in");
        return new ResolvedQualityTaxonomy(
            document.Id, document.Version, ComputeDigest(document), document.Axes, document.Aspects);
    }

    private static QualityTaxonomyDocument ReadBuiltIn()
    {
        var assembly = typeof(QualityTaxonomyCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        return JsonSerializer.Deserialize<QualityTaxonomyDocument>(stream, QualityTaxonomyJson.Options)
            ?? throw new JsonException("The built-in quality taxonomy catalogue is empty.");
    }

    private static string ComputeDigest(QualityTaxonomyDocument document)
    {
        var canonical = JsonSerializer.Serialize(document, QualityTaxonomyJson.Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void Validate(QualityTaxonomyDocument document, string source)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.Version) || document.Axes is not { Count: > 0 } ||
            document.Aspects is null)
            throw new JsonException($"Quality taxonomy catalogue '{source}' has an unsupported contract.");
        if (document.Axes.GroupBy(axis => axis.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate axis ids.");
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Quality taxonomy catalogue '{source}' contains duplicate aspect ids.");
        foreach (var axis in document.Axes)
        {
            if (axis.Terms.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new JsonException(
                    $"Quality taxonomy catalogue '{source}' axis '{axis.Id}' contains duplicate term ids.");
        }
    }
}
