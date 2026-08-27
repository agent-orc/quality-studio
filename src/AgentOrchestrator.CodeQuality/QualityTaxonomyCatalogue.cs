using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The <c>quality-studio/core</c> term catalogue (dossier docs/operations/data-model-taxonomy T1,
/// section 6). Term meanings are versioned independently of the JSON envelope by SemVer, so a
/// major bump changes or removes meaning while a minor bump only adds a term or alias.
/// </summary>
public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string TaxonomyId,
    [property: JsonPropertyOrder(3)] string Version,
    [property: JsonPropertyOrder(4)] QualityTaxonomyAxes Axes,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityTaxonomyAspect> Aspects);

public sealed record QualityTaxonomyAxes(
    [property: JsonPropertyOrder(0)] IReadOnlyList<QualityTaxonomyTerm> ProducerKind,
    [property: JsonPropertyOrder(1)] IReadOnlyList<QualityTaxonomyTerm> EvidenceStatus,
    [property: JsonPropertyOrder(2)] IReadOnlyList<QualityTaxonomyTerm> Assessment,
    [property: JsonPropertyOrder(3)] IReadOnlyList<QualityTaxonomyTerm> Change,
    [property: JsonPropertyOrder(4)] IReadOnlyList<QualityTaxonomyTerm> Decision,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityTaxonomyTerm> Severity,
    [property: JsonPropertyOrder(6)] IReadOnlyList<QualityTaxonomyTerm> Lifecycle,
    [property: JsonPropertyOrder(7)] IReadOnlyList<QualityTaxonomyTerm> EvidenceKind);

public sealed record QualityTaxonomyTerm(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Description,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Deprecated = false,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Replacement = null);

public sealed record QualityTaxonomyAspect(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Title,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MigrationAlias = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Deprecated = false);

public static class QualityTaxonomyCatalogue
{
    public const string TaxonomyId = "quality-studio/core";
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public static Lazy<QualityTaxonomyCatalogueDocument> Current { get; } = new(LoadBuiltIn);

    public static JsonSerializerOptions Options { get; } = Create();

    public static QualityTaxonomyCatalogueDocument LoadBuiltIn()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy core catalogue is unavailable.");
        var document = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(stream, Options)
            ?? throw new JsonException("The built-in quality taxonomy core catalogue is empty.");
        Validate(document);
        return document;
    }

    /// <summary>SHA-256 digest of the canonical serialization, pinned by every observation that references this catalogue.</summary>
    public static string ComputeDigest(QualityTaxonomyCatalogueDocument document)
    {
        var canonical = JsonSerializer.Serialize(document, Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsKnownAspect(QualityTaxonomyCatalogueDocument document, string aspectId) =>
        document.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));

    public static bool TryResolveAspectByMigrationAlias(
        QualityTaxonomyCatalogueDocument document, string legacyAspectId, out string coreAspectId)
    {
        var match = document.Aspects.FirstOrDefault(aspect =>
            string.Equals(aspect.MigrationAlias, legacyAspectId, StringComparison.Ordinal));
        coreAspectId = match?.Id ?? string.Empty;
        return match is not null;
    }

    private static void Validate(QualityTaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != 1 || !string.Equals(document.TaxonomyId, TaxonomyId, StringComparison.Ordinal))
        {
            throw new JsonException("The quality taxonomy core catalogue has an unsupported contract.");
        }

        if (document.Aspects.Count == 0)
        {
            throw new JsonException("The quality taxonomy core catalogue must declare at least one aspect.");
        }

        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new JsonException("The quality taxonomy core catalogue contains duplicate aspect ids.");
        }

        var axes = new[]
        {
            document.Axes.ProducerKind, document.Axes.EvidenceStatus, document.Axes.Assessment,
            document.Axes.Change, document.Axes.Decision, document.Axes.Severity,
            document.Axes.Lifecycle, document.Axes.EvidenceKind,
        };
        foreach (var axis in axes)
        {
            if (axis.Count == 0)
            {
                throw new JsonException("The quality taxonomy core catalogue must declare at least one term per axis.");
            }

            if (axis.GroupBy(term => term.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new JsonException("The quality taxonomy core catalogue contains duplicate term ids in one axis.");
            }
        }
    }

    private static JsonSerializerOptions Create() =>
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
}
