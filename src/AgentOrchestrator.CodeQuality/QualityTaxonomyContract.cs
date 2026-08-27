using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

[JsonConverter(typeof(QualityTermJsonConverter))]
public readonly record struct QualityTerm(string Value)
{
    public static implicit operator QualityTerm(string value) => new(value);

    public override string ToString() => Value;
}

public sealed class QualityTermJsonConverter : JsonConverter<QualityTerm>
{
    public override QualityTerm Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return !string.IsNullOrWhiteSpace(value)
            ? new QualityTerm(value)
            : throw new JsonException("Quality taxonomy term values must be non-empty strings.");
    }

    public override void Write(Utf8JsonWriter writer, QualityTerm value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Value))
            throw new JsonException("Quality taxonomy term values must be non-empty strings.");
        writer.WriteStringValue(value.Value);
    }
}

public abstract record QualityExtensibleContract
{
    [JsonPropertyOrder(90), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, JsonElement>? Extensions { get; init; }
}

public sealed record QualityTaxonomyCatalogueDocument : QualityExtensibleContract
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string Id { get; init; }

    [JsonPropertyOrder(3)]
    public required string Version { get; init; }

    [JsonPropertyOrder(4)]
    public required string Description { get; init; }

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<QualityTaxonomyAxisDefinition> Axes { get; init; }

    [JsonPropertyOrder(6)]
    public required IReadOnlyList<QualityAspectDefinition> Aspects { get; init; }
}

public sealed record QualityTaxonomyAxisDefinition : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    public required int Order { get; init; }

    public required IReadOnlyList<QualityTaxonomyTermDefinition> Terms { get; init; }
}

public sealed record QualityTaxonomyTermDefinition : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required int Order { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required bool Deprecated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replacement { get; init; }
}

public sealed record QualityAspectDefinition : QualityExtensibleContract
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required int Order { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required bool Deprecated { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replacement { get; init; }

    public required IReadOnlyList<string> AllowedAxes { get; init; }
}

public sealed record QualityContractReadResult<T>(
    int SchemaVersion,
    bool IsSupported,
    T? Value,
    JsonElement Raw,
    string? UnsupportedReason) where T : class;

public static class QualityTaxonomyJson
{
    private const string CoreResource =
        "AgentOrchestrator.CodeQuality.catalogues.quality-taxonomy.core.v1.json";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityTaxonomyCatalogueDocument catalogue)
    {
        Validate(catalogue);
        return JsonSerializer.Serialize(catalogue, Options);
    }

    public static QualityContractReadResult<QualityTaxonomyCatalogueDocument> Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        var schemaVersion = ReadSchemaVersion(raw, "Quality taxonomy catalogue");
        if (schemaVersion != QualityTaxonomyCatalogueDocument.CurrentSchemaVersion)
        {
            return new QualityContractReadResult<QualityTaxonomyCatalogueDocument>(
                schemaVersion,
                false,
                null,
                raw,
                $"Unsupported quality taxonomy schemaVersion '{schemaVersion}'.");
        }

        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(raw, Options)
            ?? throw new JsonException("Quality taxonomy catalogue must be a JSON object.");
        Validate(catalogue);
        return new QualityContractReadResult<QualityTaxonomyCatalogueDocument>(
            schemaVersion, true, catalogue, raw, null);
    }

    public static QualityTaxonomyCatalogueDocument LoadCore()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CoreResource)
            ?? throw new InvalidOperationException($"Embedded taxonomy catalogue '{CoreResource}' was not found.");
        using var reader = new StreamReader(stream);
        var result = Read(reader.ReadToEnd());
        return result.Value ?? throw new InvalidOperationException(result.UnsupportedReason);
    }

    internal static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    internal static int ReadSchemaVersion(JsonElement root, string label)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException($"{label} must be a JSON object.");
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion))
            throw new JsonException($"{label} schemaVersion must be an integer.");
        return schemaVersion;
    }

    internal static void Validate(QualityTaxonomyCatalogueDocument catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (catalogue.SchemaVersion != QualityTaxonomyCatalogueDocument.CurrentSchemaVersion ||
            !string.Equals(catalogue.Schema, QualityTaxonomyCatalogueDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{catalogue.SchemaVersion}'.");
        if (catalogue.Axes is null || catalogue.Aspects is null)
            throw new JsonException("Quality taxonomy axes and aspects are required.");
        RequireUnique(catalogue.Axes.Select(axis => axis.Id), "axis id");
        RequireUnique(catalogue.Axes.Select(axis => axis.Order), "axis order");
        RequireUnique(catalogue.Aspects.Select(aspect => aspect.Id), "aspect id");
        RequireUnique(catalogue.Aspects.Select(aspect => aspect.Order), "aspect order");
        foreach (var axis in catalogue.Axes)
        {
            RequireUnique(axis.Terms.Select(term => term.Id), $"term id in axis '{axis.Id}'");
            RequireUnique(axis.Terms.Select(term => term.Order), $"term order in axis '{axis.Id}'");
        }

        var axisIds = catalogue.Axes.Select(axis => axis.Id).ToHashSet(StringComparer.Ordinal);
        if (!string.Equals(catalogue.Id, QualityTaxonomyCatalogueDocument.CoreId, StringComparison.Ordinal))
        {
            axisIds.UnionWith(["producer-kind", "evidence-status", "assessment", "change", "decision",
                "severity", "lifecycle", "evidence-kind"]);
        }
        foreach (var aspect in catalogue.Aspects)
        {
            if (aspect.AllowedAxes.Count == 0 || aspect.AllowedAxes.Any(axis => !axisIds.Contains(axis)))
                throw new JsonException($"Aspect '{aspect.Id}' references an unknown or empty allowed axis.");
        }
    }

    private static void RequireUnique<T>(IEnumerable<T> values, string label) where T : notnull
    {
        var seen = new HashSet<T>();
        foreach (var value in values)
        {
            if (!seen.Add(value)) throw new JsonException($"Duplicate quality taxonomy {label} '{value}'.");
        }
    }
}

/// <summary>
/// Resolves only explicitly installed catalogues. Unknown extension aspects remain visible in
/// observation payloads but are ineligible for aggregation until their catalogue is installed.
/// </summary>
public sealed class QualityTaxonomyRegistry
{
    private readonly Dictionary<string, QualityAspectDefinition> aspects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> axisTerms = new(StringComparer.Ordinal);

    public QualityTaxonomyRegistry(IEnumerable<QualityTaxonomyCatalogueDocument>? extensionCatalogues = null)
    {
        Core = QualityTaxonomyJson.LoadCore();
        Add(Core);
        foreach (var catalogue in extensionCatalogues ?? []) Add(catalogue);
    }

    public QualityTaxonomyCatalogueDocument Core { get; }

    public bool TryResolveAspect(string aspectId, out QualityAspectDefinition? aspect) =>
        aspects.TryGetValue(aspectId, out aspect);

    public bool CanAggregate(string aspectId, string axis) =>
        aspects.TryGetValue(aspectId, out var aspect) && aspect.AllowedAxes.Contains(axis, StringComparer.Ordinal);

    public bool CanAggregate(string aspectId, string axis, string term) =>
        CanAggregate(aspectId, axis) &&
        axisTerms.TryGetValue(axis, out var terms) && terms.Contains(term);

    private void Add(QualityTaxonomyCatalogueDocument catalogue)
    {
        QualityTaxonomyJson.Validate(catalogue);
        var isCore = string.Equals(catalogue.Id, QualityTaxonomyCatalogueDocument.CoreId, StringComparison.Ordinal);
        var extensionPrefix = catalogue.Id.Split('/', 2)[0] + ":";
        foreach (var axis in catalogue.Axes)
        {
            if (!axisTerms.TryAdd(axis.Id, axis.Terms.Select(term => term.Id).ToHashSet(StringComparer.Ordinal)))
                throw new InvalidOperationException($"Installed catalogue '{catalogue.Id}' redefines quality axis '{axis.Id}'.");
        }
        foreach (var aspect in catalogue.Aspects)
        {
            if (!isCore && !aspect.Id.StartsWith(extensionPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Extension aspect '{aspect.Id}' must use catalogue prefix '{extensionPrefix}'.");
            if (!aspects.TryAdd(aspect.Id, aspect))
                throw new InvalidOperationException($"Duplicate installed quality aspect '{aspect.Id}'.");
        }
    }
}
