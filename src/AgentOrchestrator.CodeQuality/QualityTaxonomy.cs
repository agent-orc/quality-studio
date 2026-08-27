using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityTaxonomyTermKind
{
    ProducerKind,
    EvidenceStatus,
    Assessment,
    Change,
    Decision,
    Severity,
    Lifecycle,
    EvidenceKind,
    Aspect,
}

public enum QualityTaxonomyAxis
{
    Producer,
    EvidenceStatus,
    Assessment,
    Change,
    Decision,
    Severity,
    Lifecycle,
    Evidence,
}

public sealed record QualityTaxonomyTerm(
    string Id,
    QualityTaxonomyTermKind Kind,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    IReadOnlyList<QualityTaxonomyAxis> AllowedAxes,
    string? Replacement = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
}

public static class QualityTaxonomyJson
{
    private const string CoreResourceSuffix = "catalogues.quality-studio-core.v1.json";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityTaxonomyDocument catalogue)
    {
        Validate(catalogue);
        return JsonSerializer.Serialize(catalogue, Options);
    }

    public static QualityTaxonomyDocument Deserialize(string json)
    {
        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyDocument>(json, Options)
            ?? throw new JsonException("Quality taxonomy must be a JSON object.");
        Validate(catalogue);
        return catalogue;
    }

    public static QualityTaxonomyDocument LoadCoreCatalogue()
    {
        var assembly = typeof(QualityTaxonomyJson).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is unavailable.");
        using var reader = new StreamReader(stream);
        return Deserialize(reader.ReadToEnd());
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void Validate(QualityTaxonomyDocument catalogue)
    {
        if (catalogue.SchemaVersion != QualityTaxonomyDocument.CurrentSchemaVersion ||
            !string.Equals(catalogue.Schema, QualityTaxonomyDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{catalogue.SchemaVersion}'.");
        }

        var duplicateTerm = catalogue.Terms
            .GroupBy(term => (term.Kind, term.Id))
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateTerm is not null)
        {
            throw new JsonException(
                $"Quality taxonomy term '{duplicateTerm.Value.Kind}/{duplicateTerm.Value.Id}' is duplicated.");
        }

        var duplicateOrder = catalogue.Terms
            .GroupBy(term => (term.Kind, term.Order))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOrder is not null)
        {
            throw new JsonException(
                $"Quality taxonomy order '{duplicateOrder.Key.Order}' is duplicated for '{duplicateOrder.Key.Kind}'.");
        }

        var knownIds = catalogue.Terms.Select(term => (term.Kind, term.Id)).ToHashSet();
        var missingReplacement = catalogue.Terms.FirstOrDefault(term =>
            term.Replacement is not null && !knownIds.Contains((term.Kind, term.Replacement)));
        if (missingReplacement is not null)
        {
            throw new JsonException(
                $"Quality taxonomy replacement '{missingReplacement.Replacement}' for '{missingReplacement.Id}' is not declared.");
        }
    }
}

/// <summary>
/// Resolves only terms from explicitly installed catalogues. Unknown extension terms remain
/// visible in contracts but are not eligible for core aggregation.
/// </summary>
public sealed class QualityTaxonomyRegistry(IEnumerable<QualityTaxonomyDocument> catalogues)
{
    private readonly IReadOnlyDictionary<string, QualityTaxonomyDocument> _catalogues = catalogues
        .ToDictionary(catalogue => $"{catalogue.Id}@{catalogue.Version}", StringComparer.Ordinal);

    public bool CanAggregateAspect(QualityTaxonomyReference reference, string aspectId) =>
        _catalogues.TryGetValue($"{reference.Id}@{reference.Version}", out var catalogue) &&
        catalogue.Terms.Any(term =>
            term.Kind == QualityTaxonomyTermKind.Aspect &&
            string.Equals(term.Id, aspectId, StringComparison.Ordinal) &&
            term.AllowedAxes.Contains(QualityTaxonomyAxis.Assessment));
}
