using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityTaxonomyCatalogue
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreCatalogueId = "quality-studio/core";
    public const string CoreCatalogueVersion = "1.0.0";
    public const string DigestCanonicalization = "quality-studio-taxonomy-catalogue-v1";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string Id { get; init; }

    [JsonPropertyOrder(3)]
    public required string Version { get; init; }

    [JsonPropertyOrder(4)]
    public required IReadOnlyList<QualityTaxonomyAxis> Axes { get; init; }

    [JsonPropertyOrder(5)]
    public IReadOnlyList<QualityTaxonomyAspect> Aspects { get; init; } = [];

    public int Major => ParseMajor(Version);

    public static int ParseMajor(string semanticVersion) =>
        int.Parse(semanticVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture);

    public bool SupportsMajorOf(string candidateVersion) => Major == ParseMajor(candidateVersion);

    public QualityTaxonomyAxis? FindAxis(string axisId) =>
        Axes.FirstOrDefault(axis => string.Equals(axis.Id, axisId, StringComparison.Ordinal));

    public (QualityTaxonomyAxis Axis, QualityTaxonomyTerm Term, bool IsAlias)? ResolveTerm(string axisId, string termOrAlias)
    {
        var axis = FindAxis(axisId);
        if (axis is null) return null;
        foreach (var term in axis.Terms)
        {
            if (string.Equals(term.Id, termOrAlias, StringComparison.Ordinal)) return (axis, term, false);
            if (term.Aliases.Contains(termOrAlias, StringComparer.Ordinal)) return (axis, term, true);
        }

        return null;
    }

    public string ComputeDigest() =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(DigestCanonicalization + "\0" + JsonSerializer.Serialize(this, QualityTaxonomyJson.CanonicalOptions))));
}

public sealed record QualityTaxonomyAxis
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Rule { get; init; }

    [JsonPropertyOrder(3)]
    public required IReadOnlyList<QualityTaxonomyTerm> Terms { get; init; }
}

public sealed record QualityTaxonomyTerm
{
    [JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyOrder(2)]
    public IReadOnlyList<string> Aliases { get; init; } = [];

    [JsonPropertyOrder(3)]
    public bool Deprecated { get; init; }
}

public sealed record QualityTaxonomyAspect(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentSource = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MigrationAlias = null);

public enum QualityTaxonomyMajorCompatibility
{
    Supported,
    UnsupportedMajor,
}

public static class QualityTaxonomyJson
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    public static JsonSerializerOptions CanonicalOptions { get; } = CreateOptions(indented: false);

    public static string Serialize(QualityTaxonomyCatalogue catalogue) =>
        JsonSerializer.Serialize(catalogue, Options);

    public static QualityTaxonomyCatalogue Deserialize(string json) =>
        JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(json, Options)
        ?? throw new JsonException("Quality taxonomy catalogue must be a JSON object.");

    public static QualityTaxonomyCatalogue LoadCore()
    {
        var assembly = typeof(QualityTaxonomyJson).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The core quality taxonomy catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Deserialize(reader.ReadToEnd());
    }

    /// <summary>
    /// Evaluates compatibility without discarding the caller's raw data; callers keep the
    /// original JSON regardless of the result and only gate aggregation on it.
    /// </summary>
    public static QualityTaxonomyMajorCompatibility EvaluateMajorCompatibility(
        QualityTaxonomyCatalogue catalogue, string candidateVersion) =>
        catalogue.SupportsMajorOf(candidateVersion)
            ? QualityTaxonomyMajorCompatibility.Supported
            : QualityTaxonomyMajorCompatibility.UnsupportedMajor;

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
