using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyTerm(
    string Id,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? Replacement = null,
    string? Description = null);

public sealed record QualityTaxonomyAxis(IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityAspectDefinition(
    string Id,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? Replacement = null,
    string? Description = null);

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyDictionary<string, QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityAspectDefinition> Aspects);

public static class QualityTaxonomyJson
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";

    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(QualityTaxonomyDocument document) =>
        JsonSerializer.Serialize(document, Options);

    public static QualityTaxonomyDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<QualityTaxonomyDocument>(json, Options)
        ?? throw new JsonException("Quality taxonomy document must be a JSON object.");

    public static string Digest(QualityTaxonomyDocument document) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(document))));

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };
        return options;
    }
}

/// <summary>
/// Loads and indexes the built-in <c>quality-studio/core</c> taxonomy catalogue. The catalogue
/// is an independently versioned term meaning layer: it pins axis terms and namespaced aspect
/// ids, separate from the JSON structure schema version and from any review profile or policy.
/// </summary>
public static class QualityTaxonomyCatalogue
{
    public const string CoreCatalogueId = "quality-studio/core";
    private const string ResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    private static readonly Lazy<(QualityTaxonomyDocument Document, string Digest)> Core = new(LoadCore);

    public static QualityTaxonomyDocument CoreDocument => Core.Value.Document;

    public static string CoreDigest => Core.Value.Digest;

    public static bool IsKnownAxisTerm(string axis, string termId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axis);
        ArgumentException.ThrowIfNullOrWhiteSpace(termId);
        return Core.Value.Document.Axes.TryGetValue(axis, out var definition) &&
               definition.Terms.Any(term =>
                   string.Equals(term.Id, termId, StringComparison.Ordinal) ||
                   (term.Aliases?.Contains(termId, StringComparer.Ordinal) ?? false));
    }

    public static bool IsCoreAspect(string aspectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspectId);
        return Core.Value.Document.Aspects.Any(aspect =>
            string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Resolves a legacy free-form aspect string (for example <c>correctness</c>) to its
    /// namespaced core id (for example <c>code.correctness</c>). Returns <c>null</c> when the
    /// value is neither a core id nor a known migration alias, so callers can keep it visible
    /// as an unrecognized term instead of silently coercing it.
    /// </summary>
    public static string? ResolveAspectAlias(string legacyAspectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyAspectId);
        if (IsCoreAspect(legacyAspectId)) return legacyAspectId;
        return Core.Value.Document.Aspects
            .FirstOrDefault(aspect => aspect.Aliases?.Contains(legacyAspectId, StringComparer.Ordinal) ?? false)
            ?.Id;
    }

    private static (QualityTaxonomyDocument, string) LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var document = QualityTaxonomyJson.Deserialize(json);
        Validate(document);
        return (document, QualityTaxonomyJson.Digest(document));
    }

    private static void Validate(QualityTaxonomyDocument document)
    {
        if (document.SchemaVersion != QualityTaxonomyJson.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityTaxonomyJson.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{document.SchemaVersion}'.");
        if (document.Axes.Count == 0)
            throw new JsonException("Quality taxonomy catalogue must declare at least one axis.");
        if (document.Aspects
            .GroupBy(aspect => aspect.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new JsonException("Quality taxonomy catalogue contains duplicate aspect ids.");
    }
}
