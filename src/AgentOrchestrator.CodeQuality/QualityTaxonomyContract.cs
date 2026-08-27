using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyTerm(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null);

public sealed record QualityTaxonomyAxis(
    string Id,
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    string? Description = null);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null);

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreCatalogueId = "quality-studio/core";
}

/// <summary>A pinned reference to an installed taxonomy catalogue, as embedded in an observation.</summary>
public sealed record QualityTaxonomyReference(string Id, string Version, string Digest);

/// <summary>Raised when a taxonomy document declares a schema major this build does not understand.
/// Carries the raw JSON so the caller can quarantine the document instead of losing it.</summary>
public sealed class UnsupportedQualityTaxonomyMajorException(string message, string rawJson) : Exception(message)
{
    public string RawJson { get; } = rawJson;
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static QualityTaxonomyCatalogueDocument Deserialize(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        ValidateContractVersion(parsed.RootElement, json);
        var document = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(json, Options)
            ?? throw new JsonException("Quality taxonomy catalogue must be a JSON object.");
        Validate(document);
        return document;
    }

    public static string Serialize(QualityTaxonomyCatalogueDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    private static void Validate(QualityTaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != QualityTaxonomyCatalogueDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityTaxonomyCatalogueDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{document.SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(document.Id) || string.IsNullOrWhiteSpace(document.Version))
            throw new JsonException("Quality taxonomy catalogue must declare id and version.");
        if (document.Axes.GroupBy(axis => axis.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Quality taxonomy catalogue contains duplicate axis ids.");
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Quality taxonomy catalogue contains duplicate aspect ids.");
    }

    private static void ValidateContractVersion(JsonElement root, string rawJson)
    {
        var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaVersionElement) &&
                             schemaVersionElement.ValueKind == JsonValueKind.Number
            ? schemaVersionElement.GetInt32()
            : -1;
        var schema = root.TryGetProperty("$schema", out var schemaElement) ? schemaElement.GetString() : null;
        if (schemaVersion != QualityTaxonomyCatalogueDocument.CurrentSchemaVersion ||
            !string.Equals(schema, QualityTaxonomyCatalogueDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new UnsupportedQualityTaxonomyMajorException(
                $"Unsupported quality taxonomy schemaVersion '{schemaVersion}'.", rawJson);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        return options;
    }
}

/// <summary>The built-in <c>quality-studio/core</c> taxonomy catalogue, embedded as a resource.
/// T1 only exposes this catalogue for lookup and digest pinning; no writer consumes it yet.</summary>
public static class QualityTaxonomyCoreCatalogue
{
    private const string ResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    private static readonly Lazy<(QualityTaxonomyCatalogueDocument Document, string Digest)> Loaded = new(Load);

    public static QualityTaxonomyCatalogueDocument Document => Loaded.Value.Document;

    public static string Digest => Loaded.Value.Digest;

    public static QualityTaxonomyReference Reference() => new(Document.Id, Document.Version, Digest);

    public static QualityTaxonomyAspect? FindAspect(string aspectIdOrAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspectIdOrAlias);
        foreach (var aspect in Document.Aspects)
        {
            if (string.Equals(aspect.Id, aspectIdOrAlias, StringComparison.Ordinal)) return aspect;
            if (aspect.Aliases?.Contains(aspectIdOrAlias, StringComparer.Ordinal) == true) return aspect;
        }
        return null;
    }

    public static QualityTaxonomyTerm? FindTerm(string axisId, string termIdOrAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        ArgumentException.ThrowIfNullOrWhiteSpace(termIdOrAlias);
        var axis = Document.Axes.FirstOrDefault(candidate => string.Equals(candidate.Id, axisId, StringComparison.Ordinal));
        if (axis is null) return null;
        foreach (var term in axis.Terms)
        {
            if (string.Equals(term.Id, termIdOrAlias, StringComparison.Ordinal)) return term;
            if (term.Aliases?.Contains(termIdOrAlias, StringComparer.Ordinal) == true) return term;
        }
        return null;
    }

    private static (QualityTaxonomyCatalogueDocument, string) Load()
    {
        var assembly = typeof(QualityTaxonomyCoreCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var raw = reader.ReadToEnd();
        var document = QualityTaxonomyJson.Deserialize(raw);
        if (!string.Equals(document.Id, QualityTaxonomyCatalogueDocument.CoreCatalogueId, StringComparison.Ordinal))
            throw new InvalidOperationException("The built-in quality taxonomy catalogue has an unexpected id.");
        var digest = "sha256:" +
                     Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(QualityTaxonomyJson.Serialize(document))));
        return (document, digest);
    }
}
