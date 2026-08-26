using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    IReadOnlyList<string> AllowedAxes,
    string? Replacement = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    IReadOnlyList<string> AllowedAssessmentAxes,
    string? Replacement = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyTerms(
    IReadOnlyList<QualityTaxonomyTerm> ProducerKinds,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceStatuses,
    IReadOnlyList<QualityTaxonomyTerm> Assessments,
    IReadOnlyList<QualityTaxonomyTerm> Changes,
    IReadOnlyList<QualityTaxonomyTerm> Decisions,
    IReadOnlyList<QualityTaxonomyTerm> Severities,
    IReadOnlyList<QualityTaxonomyTerm> Lifecycles,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceKinds);

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    QualityTaxonomyTerms Terms,
    IReadOnlyList<QualityTaxonomyAspect> Aspects,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
}

public sealed record ResolvedQualityTaxonomy(
    QualityTaxonomyDocument Document,
    string Digest)
{
    public bool IsCoreAspect(string aspectId) =>
        Document.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));

    public string? ResolveCoreAspectAlias(string alias) =>
        Document.Aspects.FirstOrDefault(aspect =>
            aspect.Aliases.Contains(alias, StringComparer.Ordinal))?.Id;
}

public static class QualityTaxonomyCatalogue
{
    private const string CoreResourceSuffix = "catalogues.quality-studio-core.v1.json";
    private static readonly Lazy<ResolvedQualityTaxonomy> CoreCatalogue = new(LoadCore);

    public static ResolvedQualityTaxonomy Core => CoreCatalogue.Value;

    private static ResolvedQualityTaxonomy LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var document = JsonSerializer.Deserialize<QualityTaxonomyDocument>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The built-in quality taxonomy catalogue is empty.");

        Validate(document);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ResolvedQualityTaxonomy(document, digest);
    }

    private static void Validate(QualityTaxonomyDocument document)
    {
        if (document.SchemaVersion != QualityTaxonomyDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityTaxonomyDocument.SchemaId, StringComparison.Ordinal) ||
            !string.Equals(document.Id, QualityTaxonomyDocument.CoreId, StringComparison.Ordinal) ||
            !string.Equals(document.Version, QualityTaxonomyDocument.CoreVersion, StringComparison.Ordinal))
        {
            throw new JsonException("The built-in quality taxonomy catalogue has an unsupported contract.");
        }

        var termGroups = new[]
        {
            document.Terms.ProducerKinds,
            document.Terms.EvidenceStatuses,
            document.Terms.Assessments,
            document.Terms.Changes,
            document.Terms.Decisions,
            document.Terms.Severities,
            document.Terms.Lifecycles,
            document.Terms.EvidenceKinds,
        };
        if (termGroups.Any(group => group.Count == 0 || HasDuplicateIdsOrOrder(group)))
            throw new JsonException("The built-in quality taxonomy catalogue contains duplicate or empty terms.");
        if (document.Aspects.Count == 0 ||
            document.Aspects.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            document.Aspects.GroupBy(item => item.Order).Any(group => group.Count() > 1))
            throw new JsonException("The built-in quality taxonomy catalogue contains duplicate or empty aspects.");
    }

    private static bool HasDuplicateIdsOrOrder(IReadOnlyList<QualityTaxonomyTerm> terms) =>
        terms.GroupBy(item => item.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
        terms.GroupBy(item => item.Order).Any(group => group.Count() > 1);
}
