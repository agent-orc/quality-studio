using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// A term catalogue document. The catalogue carries its own Semantic Version so term meanings
/// can change independently of the JSON structure that refers to them.
/// </summary>
public sealed record TaxonomyCatalogueDocument
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = QualityTaxonomyCatalogue.SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = QualityTaxonomyCatalogue.CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string Id { get; init; }

    [JsonPropertyOrder(3)]
    public required string Version { get; init; }

    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prefix { get; init; }

    [JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyOrder(7)]
    public required IReadOnlyList<TaxonomyAxis> Axes { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<TaxonomyObject> Objects { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<TaxonomyAspect> Aspects { get; init; }
}

public sealed record TaxonomyAxis(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Description,
    [property: JsonPropertyOrder(2)] IReadOnlyList<TaxonomyTerm> Terms,
    [property: JsonPropertyOrder(3)] bool Ordered = false);

public sealed record TaxonomyTerm(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Description,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyOrder(3)] bool Deprecated = false,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacedBy = null);

public sealed record TaxonomyObject(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string> Axes,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

public sealed record TaxonomyAspect(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Family,
    [property: JsonPropertyOrder(2)] string Description,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Aliases = null,
    [property: JsonPropertyOrder(4)] bool Deprecated = false,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplacedBy = null);

/// <summary>How a value relates to the installed catalogues.</summary>
public enum TaxonomyTermStatus
{
    /// <summary>The value is a term id in an installed catalogue.</summary>
    Canonical,

    /// <summary>The value is a declared spelling variant of a term in an installed catalogue.</summary>
    Alias,

    /// <summary>The value is prefixed by an installed extension catalogue that declares it.</summary>
    Extension,

    /// <summary>No installed catalogue declares the value. It stays visible and is excluded from aggregates.</summary>
    Unrecognized,
}

public sealed record TaxonomyTermResolution(
    string Axis,
    string Value,
    string? CanonicalId,
    TaxonomyTermStatus Status,
    bool Deprecated = false,
    string? ReplacedBy = null)
{
    /// <summary>The UI label for a value no installed catalogue understands.</summary>
    public const string UnrecognizedLabel = "unrecognized term";

    public bool IsKnown => Status is TaxonomyTermStatus.Canonical or TaxonomyTermStatus.Alias;

    /// <summary>
    /// Only core terms feed core aggregates. Extension and unrecognized terms round-trip and render,
    /// but never silently become a core term.
    /// </summary>
    public bool ParticipatesInCoreAggregation => IsKnown;

    public string DisplayLabel => Status switch
    {
        TaxonomyTermStatus.Unrecognized => UnrecognizedLabel,
        _ => CanonicalId ?? Value,
    };
}

/// <summary>One loaded catalogue together with the digest observations pin it by.</summary>
public sealed class QualityTaxonomyCatalogue
{
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
    public const string CoreCatalogueId = "quality-studio/core";
    private const string CoreResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    private static readonly Lazy<QualityTaxonomyCatalogue> LazyCore = new(ReadBuiltInCore, isThreadSafe: true);

    private readonly Dictionary<string, AxisIndex> _axes;
    private readonly Dictionary<string, TaxonomyAspect> _aspectsById;
    private readonly Dictionary<string, TaxonomyAspect> _aspectsByAlias;
    private readonly Dictionary<string, IReadOnlyList<string>> _objectAxes;

    private QualityTaxonomyCatalogue(TaxonomyCatalogueDocument document, string digest)
    {
        Document = document;
        Digest = digest;
        _axes = document.Axes.ToDictionary(axis => axis.Id, axis => new AxisIndex(axis), StringComparer.Ordinal);
        _aspectsById = document.Aspects.ToDictionary(aspect => aspect.Id, StringComparer.Ordinal);
        _aspectsByAlias = new Dictionary<string, TaxonomyAspect>(StringComparer.Ordinal);
        foreach (var aspect in document.Aspects)
        {
            foreach (var alias in aspect.Aliases ?? [])
            {
                if (!_aspectsByAlias.TryAdd(alias, aspect))
                    throw new JsonException($"Catalogue '{document.Id}' declares aspect alias '{alias}' more than once.");
            }
        }

        _objectAxes = document.Objects.ToDictionary(item => item.Id, item => item.Axes, StringComparer.Ordinal);
    }

    /// <summary>The built-in core catalogue that ships with this assembly.</summary>
    public static QualityTaxonomyCatalogue Core => LazyCore.Value;

    public TaxonomyCatalogueDocument Document { get; }

    /// <summary>SHA-256 of the catalogue text after BOM and line-ending normalization.</summary>
    public string Digest { get; }

    public string Id => Document.Id;

    public string Version => Document.Version;

    public string? Prefix => Document.Prefix;

    public int MajorVersion => int.Parse(Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);

    public TaxonomyCatalogueReference Reference => new(Id, Version, Digest);

    public IReadOnlyList<TaxonomyAspect> Aspects => Document.Aspects;

    public static QualityTaxonomyCatalogue Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var normalized = Normalize(json);
        var document = JsonSerializer.Deserialize<TaxonomyCatalogueDocument>(normalized, QualityTaxonomyJson.Options)
            ?? throw new JsonException("A term catalogue must be a JSON object.");
        Validate(document);
        return new QualityTaxonomyCatalogue(document, ComputeDigest(normalized));
    }

    public bool TryGetAxis(string axisId, out TaxonomyAxis axis)
    {
        if (_axes.TryGetValue(axisId, out var index))
        {
            axis = index.Axis;
            return true;
        }

        axis = null!;
        return false;
    }

    /// <summary>Resolves a value inside one axis, following declared spelling aliases.</summary>
    public TaxonomyTermResolution ResolveTerm(string axisId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(axisId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!_axes.TryGetValue(axisId, out var index))
            throw new ArgumentException($"Catalogue '{Id}' declares no axis '{axisId}'.", nameof(axisId));
        if (index.ById.TryGetValue(value, out var canonical))
            return new(axisId, value, canonical.Id, TaxonomyTermStatus.Canonical, canonical.Deprecated, canonical.ReplacedBy);
        if (index.ByAlias.TryGetValue(value, out var aliased))
            return new(axisId, value, aliased.Id, TaxonomyTermStatus.Alias, aliased.Deprecated, aliased.ReplacedBy);
        return new(axisId, value, null, TaxonomyTermStatus.Unrecognized);
    }

    public TaxonomyTermResolution ResolveAspect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (_aspectsById.TryGetValue(value, out var canonical))
            return new("aspect", value, canonical.Id, TaxonomyTermStatus.Canonical, canonical.Deprecated, canonical.ReplacedBy);
        if (_aspectsByAlias.TryGetValue(value, out var aliased))
            return new("aspect", value, aliased.Id, TaxonomyTermStatus.Alias, aliased.Deprecated, aliased.ReplacedBy);
        return new("aspect", value, null, TaxonomyTermStatus.Unrecognized);
    }

    /// <summary>The rank of an ordered term, lowest first. Unordered axes and unknown terms have no rank.</summary>
    public int? Rank(string axisId, string value)
    {
        if (!_axes.TryGetValue(axisId, out var index) || !index.Axis.Ordered) return null;
        var resolved = ResolveTerm(axisId, value);
        if (resolved.CanonicalId is null) return null;
        for (var position = 0; position < index.Axis.Terms.Count; position++)
        {
            if (string.Equals(index.Axis.Terms[position].Id, resolved.CanonicalId, StringComparison.Ordinal))
                return position;
        }

        return null;
    }

    /// <summary>Whether an object of the catalogue is allowed to carry a value on the given axis.</summary>
    public bool AxisAppliesTo(string objectId, string axisId) =>
        _objectAxes.TryGetValue(objectId, out var axes) && axes.Contains(axisId, StringComparer.Ordinal);

    public IReadOnlyList<string> AxesFor(string objectId) =>
        _objectAxes.TryGetValue(objectId, out var axes) ? axes : [];

    private static void Validate(TaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(document.Schema, SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported term catalogue schemaVersion '{document.SchemaVersion}'.");
        if (!QualityTaxonomyJson.IsSemanticVersion(document.Version))
            throw new JsonException($"Term catalogue '{document.Id}' must carry a Semantic Version.");
        if (document.Axes.Count == 0 || document.Objects.Count == 0 || document.Aspects.Count == 0)
            throw new JsonException($"Term catalogue '{document.Id}' is empty.");
        if (string.Equals(document.Id, CoreCatalogueId, StringComparison.Ordinal) && document.Prefix is not null)
            throw new JsonException("The core catalogue must not declare a term prefix.");

        var axisIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var axis in document.Axes)
        {
            if (!axisIds.Add(axis.Id))
                throw new JsonException($"Term catalogue '{document.Id}' declares axis '{axis.Id}' more than once.");
            var spellings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var term in axis.Terms)
            {
                if (!spellings.Add(term.Id))
                    throw new JsonException($"Axis '{axis.Id}' declares term '{term.Id}' more than once.");
                foreach (var alias in term.Aliases ?? [])
                {
                    if (!spellings.Add(alias))
                        throw new JsonException($"Axis '{axis.Id}' declares spelling '{alias}' more than once.");
                }

                if (term.ReplacedBy is not null && axis.Terms.All(other => !string.Equals(other.Id, term.ReplacedBy, StringComparison.Ordinal)))
                    throw new JsonException($"Term '{axis.Id}/{term.Id}' is replaced by unknown term '{term.ReplacedBy}'.");
            }
        }

        foreach (var item in document.Objects)
        {
            foreach (var axis in item.Axes)
            {
                if (!axisIds.Contains(axis))
                    throw new JsonException($"Object '{item.Id}' refers to unknown axis '{axis}'.");
            }
        }

        var aspectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aspect in document.Aspects)
        {
            if (!aspectIds.Add(aspect.Id))
                throw new JsonException($"Term catalogue '{document.Id}' declares aspect '{aspect.Id}' more than once.");
        }
    }

    private static QualityTaxonomyCatalogue ReadBuiltInCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in core term catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Load(reader.ReadToEnd());
    }

    private static string Normalize(string json) =>
        json.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ComputeDigest(string normalized) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private sealed class AxisIndex
    {
        public AxisIndex(TaxonomyAxis axis)
        {
            Axis = axis;
            ById = axis.Terms.ToDictionary(term => term.Id, StringComparer.Ordinal);
            ByAlias = new Dictionary<string, TaxonomyTerm>(StringComparer.Ordinal);
            foreach (var term in axis.Terms)
            {
                foreach (var alias in term.Aliases ?? []) ByAlias[alias] = term;
            }
        }

        public TaxonomyAxis Axis { get; }

        public Dictionary<string, TaxonomyTerm> ById { get; }

        public Dictionary<string, TaxonomyTerm> ByAlias { get; }
    }
}

public sealed record TaxonomyCatalogueReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string Digest);

public sealed record TaxonomyExtensionCatalogueReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string Digest,
    [property: JsonPropertyOrder(3)] string Prefix);

/// <summary>
/// Resolves values against one core catalogue and the extension catalogues that are installed.
/// An extension term keeps its declared prefix and can never shadow a core term.
/// </summary>
public sealed class QualityTaxonomyResolver
{
    private readonly Dictionary<string, QualityTaxonomyCatalogue> _extensionsByPrefix;

    public QualityTaxonomyResolver(
        QualityTaxonomyCatalogue? core = null,
        IEnumerable<QualityTaxonomyCatalogue>? extensions = null)
    {
        Core = core ?? QualityTaxonomyCatalogue.Core;
        _extensionsByPrefix = new Dictionary<string, QualityTaxonomyCatalogue>(StringComparer.Ordinal);
        foreach (var extension in extensions ?? [])
        {
            if (string.Equals(extension.Id, Core.Id, StringComparison.Ordinal))
                throw new ArgumentException($"Catalogue '{extension.Id}' is the core catalogue and cannot be installed as an extension.", nameof(extensions));
            if (extension.Prefix is not { Length: > 0 } prefix)
                throw new ArgumentException($"Extension catalogue '{extension.Id}' must declare a term prefix.", nameof(extensions));
            if (!_extensionsByPrefix.TryAdd(prefix, extension))
                throw new ArgumentException($"Two extension catalogues declare the prefix '{prefix}'.", nameof(extensions));
        }
    }

    public static QualityTaxonomyResolver Default { get; } = new();

    public QualityTaxonomyCatalogue Core { get; }

    public IReadOnlyCollection<QualityTaxonomyCatalogue> Extensions => _extensionsByPrefix.Values;

    public IReadOnlyList<TaxonomyExtensionCatalogueReference> ExtensionReferences =>
        _extensionsByPrefix
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TaxonomyExtensionCatalogueReference(
                pair.Value.Id, pair.Value.Version, pair.Value.Digest, pair.Key))
            .ToArray();

    /// <summary>
    /// True when an observation pinning <paramref name="reference"/> can be interpreted at all.
    /// A different major of the same catalogue is structured-but-unsupported, never reinterpreted.
    /// </summary>
    public bool SupportsTaxonomy(TaxonomyCatalogueReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.Id, Core.Id, StringComparison.Ordinal)) return false;
        return QualityTaxonomyJson.MajorOf(reference.Version) == Core.MajorVersion;
    }

    public TaxonomyTermResolution ResolveTerm(string axisId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (TrySplitPrefix(value, out var prefix, out var bare))
        {
            if (!_extensionsByPrefix.TryGetValue(prefix, out var extension))
                return new(axisId, value, null, TaxonomyTermStatus.Unrecognized);
            var inner = extension.TryGetAxis(axisId, out _)
                ? extension.ResolveTerm(axisId, bare)
                : new TaxonomyTermResolution(axisId, bare, null, TaxonomyTermStatus.Unrecognized);
            return inner.IsKnown
                ? new(axisId, value, prefix + ":" + inner.CanonicalId, TaxonomyTermStatus.Extension, inner.Deprecated,
                    inner.ReplacedBy is null ? null : prefix + ":" + inner.ReplacedBy)
                : new(axisId, value, null, TaxonomyTermStatus.Unrecognized);
        }

        return Core.ResolveTerm(axisId, value);
    }

    public TaxonomyTermResolution ResolveAspect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (TrySplitPrefix(value, out var prefix, out var bare))
        {
            if (!_extensionsByPrefix.TryGetValue(prefix, out var extension))
                return new("aspect", value, null, TaxonomyTermStatus.Unrecognized);
            var inner = extension.ResolveAspect(bare);
            return inner.IsKnown
                ? new("aspect", value, prefix + ":" + inner.CanonicalId, TaxonomyTermStatus.Extension, inner.Deprecated,
                    inner.ReplacedBy is null ? null : prefix + ":" + inner.ReplacedBy)
                : new("aspect", value, null, TaxonomyTermStatus.Unrecognized);
        }

        return Core.ResolveAspect(value);
    }

    private static bool TrySplitPrefix(string value, out string prefix, out string bare)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && separator < value.Length - 1)
        {
            prefix = value[..separator];
            bare = value[(separator + 1)..];
            return true;
        }

        prefix = string.Empty;
        bare = value;
        return false;
    }
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static bool IsSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('.');
        return parts.Length == 3 && parts.All(part =>
            part.Length > 0 && part.All(char.IsAsciiDigit) && (part == "0" || part[0] != '0'));
    }

    public static int MajorOf(string version) =>
        IsSemanticVersion(version)
            ? int.Parse(version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture)
            : throw new ArgumentException($"'{version}' is not a Semantic Version.", nameof(version));
}
