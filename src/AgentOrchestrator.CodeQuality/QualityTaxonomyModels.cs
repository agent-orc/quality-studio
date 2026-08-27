using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// quality-studio/core@1.0.0: the independently versioned term catalogue for observation
/// axes and aspects. Term meanings change under SemVer, separately from the JSON
/// structure version and from any review profile or policy version.
/// </summary>
public sealed record TaxonomyCatalogueDocument
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
    public required IReadOnlyDictionary<string, TaxonomyAxis> Axes { get; init; }

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<TaxonomyAspect> Aspects { get; init; }
}

public sealed record TaxonomyAxis(IReadOnlyList<TaxonomyTerm> Terms);

public sealed record TaxonomyTerm(
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false);

public sealed record TaxonomyAspect(
    string Id,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MigrationAliases = null);

/// <summary>
/// A catalogue pinned to its content digest. The digest, not the version string alone,
/// is what an observation records so that a term meaning can never drift silently under
/// an unchanged version label.
/// </summary>
public sealed record ResolvedTaxonomyCatalogue(TaxonomyCatalogueDocument Document, string Digest)
{
    public bool IsCoreAspect(string aspectId) =>
        Document.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));

    public bool TryResolveMigrationAlias(string legacyAspectId, out string coreAspectId)
    {
        var match = Document.Aspects.FirstOrDefault(aspect =>
            aspect.MigrationAliases?.Contains(legacyAspectId, StringComparer.Ordinal) == true);
        coreAspectId = match?.Id ?? string.Empty;
        return match is not null;
    }

    public bool TryResolveTermAlias(string axisName, string legacyTermId, out string coreTermId)
    {
        coreTermId = string.Empty;
        if (!Document.Axes.TryGetValue(axisName, out var axis)) return false;
        var match = axis.Terms.FirstOrDefault(term =>
            string.Equals(term.Id, legacyTermId, StringComparison.Ordinal) ||
            term.Aliases?.Contains(legacyTermId, StringComparer.Ordinal) == true);
        if (match is null) return false;
        coreTermId = match.Id;
        return true;
    }
}

public static class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy.v1.core.json";

    public static ResolvedTaxonomyCatalogue ResolveCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The core quality taxonomy catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();
        var document = JsonSerializer.Deserialize<TaxonomyCatalogueDocument>(json, QualityTaxonomyJson.Options)
            ?? throw new JsonException("The core quality taxonomy catalogue is empty.");
        Validate(document);
        return new ResolvedTaxonomyCatalogue(document, Digest(json));
    }

    private static string Digest(string json) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static void Validate(TaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != TaxonomyCatalogueDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, TaxonomyCatalogueDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{document.SchemaVersion}'.");
        if (document.Axes.Count == 0)
            throw new JsonException("A quality taxonomy catalogue must declare at least one axis.");
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("A quality taxonomy catalogue contains duplicate aspect ids.");
    }
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options;
    }
}
