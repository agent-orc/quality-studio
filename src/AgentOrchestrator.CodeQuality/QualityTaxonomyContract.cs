using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

[JsonConverter(typeof(JsonStringEnumConverter<ProducerKind>))]
public enum ProducerKind
{
    Agent,
    DeterministicSensor,
    Human,
    Imported,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceStatus>))]
public enum EvidenceStatus
{
    Available,
    Partial,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationAssessment>))]
public enum ObservationAssessment
{
    Pass,
    Concern,
    Fail,
    Inconclusive,
    NotApplicable,
    NotAssessed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationChange>))]
public enum ObservationChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

[JsonConverter(typeof(JsonStringEnumConverter<PolicyDecision>))]
public enum PolicyDecision
{
    Allow,
    Warn,
    Block,
    Defer,
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationEvidenceKind>))]
public enum ObservationEvidenceKind
{
    SourceCode,
    TestResult,
    RuntimeMeasurement,
    ToolResult,
    Artifact,
    Document,
    HumanAttestation,
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationLifecycleState>))]
public enum ObservationLifecycleState
{
    Open,
    AcceptedRisk,
    Waived,
    FalsePositive,
    Resolved,
}

public sealed record QualityTaxonomyTerm(
    string Id,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null)
{
    public bool Matches(string id) =>
        string.Equals(Id, id, StringComparison.Ordinal) ||
        (Aliases?.Contains(id, StringComparer.Ordinal) ?? false);
}

public sealed record QualityTaxonomyAxis(
    string Description,
    IReadOnlyList<QualityTaxonomyTerm> Terms)
{
    public bool HasTerm(string id) => Terms.Any(term => term.Matches(id));
}

public sealed record QualityTaxonomyAxes(
    QualityTaxonomyAxis ProducerKind,
    QualityTaxonomyAxis EvidenceStatus,
    QualityTaxonomyAxis Assessment,
    QualityTaxonomyAxis Change,
    QualityTaxonomyAxis Decision,
    QualityTaxonomyAxis Severity,
    QualityTaxonomyAxis Lifecycle,
    QualityTaxonomyAxis EvidenceKind);

public sealed record QualityTaxonomyIdentity(string Id, string Version);

public sealed record QualityTaxonomyAspect(
    string Id,
    string Source,
    string? MigrationAlias = null,
    bool Deprecated = false,
    string? ReplacedBy = null);

/// <summary>
/// A pinned taxonomy or extension catalogue identity. The digest is computed over the
/// canonical serialized catalogue, never stored inside the catalogue document itself.
/// </summary>
public sealed record QualityTaxonomyReference(string Id, string Version, string Digest);

public sealed record QualityTaxonomyCatalogue
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required QualityTaxonomyIdentity Taxonomy { get; init; }

    public required QualityTaxonomyAxes Axes { get; init; }

    public required IReadOnlyList<QualityTaxonomyAspect> Aspects { get; init; }

    /// <summary>
    /// True when <paramref name="aspectId"/> or one of its migration aliases is a core term.
    /// An unknown aspect id (for example an extension term) returns false and must not be
    /// silently coerced into a core term.
    /// </summary>
    public bool IsKnownAspect(string aspectId) =>
        Aspects.Any(aspect =>
            string.Equals(aspect.Id, aspectId, StringComparison.Ordinal) ||
            string.Equals(aspect.MigrationAlias, aspectId, StringComparison.Ordinal));
}

public static class QualityTaxonomyJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityTaxonomyCatalogue catalogue) =>
        JsonSerializer.Serialize(catalogue, Options);

    public static QualityTaxonomyCatalogue Deserialize(string json)
    {
        var catalogue = JsonSerializer.Deserialize<QualityTaxonomyCatalogue>(json, Options)
            ?? throw new JsonException("Quality taxonomy catalogue must be a JSON object.");
        if (catalogue.SchemaVersion != QualityTaxonomyCatalogue.CurrentSchemaVersion ||
            !string.Equals(catalogue.Schema, QualityTaxonomyCatalogue.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality taxonomy schemaVersion '{catalogue.SchemaVersion}'.");
        }
        return catalogue;
    }

    /// <summary>
    /// The taxonomy digest that observations pin. Computed over the canonical serialized
    /// catalogue so any term, alias, or aspect change produces a different digest.
    /// </summary>
    public static string Digest(QualityTaxonomyCatalogue catalogue) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(catalogue))));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

/// <summary>
/// Resolves the built-in <c>quality-studio/core@1.0.0</c> catalogue embedded in this
/// assembly. Project and global overlay catalogues are out of scope for this contract
/// foundation; only the core catalogue is resolved here.
/// </summary>
public static class QualityTaxonomyCatalogueResolver
{
    private const string BuiltInResourceSuffix = "catalogues.quality-taxonomy-core.v1.json";

    public static QualityTaxonomyCatalogue LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in quality taxonomy catalogue is unavailable.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var catalogue = QualityTaxonomyJson.Deserialize(reader.ReadToEnd());
        if (catalogue.Taxonomy.Id != QualityTaxonomyCatalogue.CoreId ||
            catalogue.Taxonomy.Version != QualityTaxonomyCatalogue.CoreVersion)
        {
            throw new InvalidOperationException(
                "The built-in quality taxonomy catalogue identity does not match quality-studio/core@1.0.0.");
        }
        return catalogue;
    }

    public static QualityTaxonomyReference Reference(QualityTaxonomyCatalogue catalogue) =>
        new(catalogue.Taxonomy.Id, catalogue.Taxonomy.Version, QualityTaxonomyJson.Digest(catalogue));
}
