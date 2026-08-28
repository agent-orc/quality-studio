using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

[JsonConverter(typeof(JsonStringEnumConverter<QualityProducerKind>))]
public enum QualityProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceStatus>))]
public enum QualityEvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityAssessment>))]
public enum QualityAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityChange>))]
public enum QualityChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityDecision>))]
public enum QualityDecision
{
    Allow,
    Warn,
    Block,
    Defer,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityLifecycleState>))]
public enum QualityLifecycleState
{
    Open,
    AcceptedRisk,
    Waived,
    FalsePositive,
    Resolved,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualityEvidenceKind>))]
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

/// <summary>Pins the exact term catalogue an observation was authored against; see quality-taxonomy.v1.</summary>
public sealed record TaxonomyReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string Digest);

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false);

public sealed record QualityTaxonomyAxis(IReadOnlyList<QualityTaxonomyTerm> Terms);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Title,
    string? Description = null,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false);

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Catalogue,
    string Version,
    IReadOnlyDictionary<string, QualityTaxonomyAxis> Axes,
    IReadOnlyList<QualityTaxonomyAspect> Aspects);

/// <summary>
/// A catalogue resolved to one exact digest, with alias and unknown-term lookups. An aspect id
/// absent from <see cref="AspectsById"/> is an extension term, not a core term, and must not
/// participate in core aggregation.
/// </summary>
public sealed class ResolvedQualityTaxonomy
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Digest { get; init; }
    public required IReadOnlyDictionary<string, QualityTaxonomyAspect> AspectsById { get; init; }
    public required IReadOnlyDictionary<string, string> AspectAliasToId { get; init; }

    public TaxonomyReference ToReference() => new(Id, Version, Digest);

    public bool IsCoreAspect(string aspectId) => AspectsById.ContainsKey(aspectId);

    public bool TryResolveAlias(string alias, out string aspectId) =>
        AspectAliasToId.TryGetValue(alias, out aspectId!);
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
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed class QualityTaxonomyCatalogueResolver
{
    public const string ProjectRelativePath = ".quality/taxonomy/core-catalogue.json";
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";
    public static readonly IReadOnlyList<string> RequiredAxes =
    [
        "producerKind", "evidenceStatus", "assessment", "change",
        "decision", "severity", "lifecycle", "evidenceKind",
    ];

    public ResolvedQualityTaxonomy ResolveBuiltIn()
    {
        var document = ReadBuiltIn();
        Validate(document, "embedded:" + BuiltInResourceSuffix);
        return Resolve(document);
    }

    public ResolvedQualityTaxonomy ResolveFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(stream, QualityTaxonomyJson.Options)
            ?? throw new JsonException($"Taxonomy catalogue '{path}' is empty.");
        Validate(document, path);
        return Resolve(document);
    }

    private static ResolvedQualityTaxonomy Resolve(QualityTaxonomyCatalogueDocument document)
    {
        var byId = document.Aspects.ToDictionary(aspect => aspect.Id, StringComparer.Ordinal);
        var aliasToId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var aspect in document.Aspects)
        {
            foreach (var alias in aspect.Aliases ?? [])
            {
                aliasToId[alias] = aspect.Id;
            }
        }

        return new ResolvedQualityTaxonomy
        {
            Id = document.Catalogue,
            Version = document.Version,
            Digest = QualityTaxonomyJson.Hash(document),
            AspectsById = byId,
            AspectAliasToId = aliasToId,
        };
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
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Catalogue) ||
            string.IsNullOrWhiteSpace(document.Version) || document.Axes is null || document.Aspects is null)
            throw new JsonException($"Taxonomy catalogue '{source}' has an unsupported contract.");
        foreach (var axis in RequiredAxes)
        {
            if (!document.Axes.TryGetValue(axis, out var definition) || definition.Terms is not { Count: > 0 })
                throw new JsonException($"Taxonomy catalogue '{source}' is missing axis '{axis}'.");
        }
        if (document.Aspects.GroupBy(aspect => aspect.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Taxonomy catalogue '{source}' contains duplicate aspect ids.");
        if (document.Aspects.Any(aspect => string.IsNullOrWhiteSpace(aspect.Id) || string.IsNullOrWhiteSpace(aspect.Title)))
            throw new JsonException($"Taxonomy catalogue '{source}' contains an invalid aspect.");
    }
}
