using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }
public enum QualityEvidenceStatus { Available, Partial, Unavailable }
public enum QualityAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }
public enum QualityChange { Improved, Regressed, Mixed, Unchanged, NoObservedDelta, Inconclusive }
public enum QualityDecision { Allow, Warn, Block, Defer }
public enum QualityLifecycleState { Open, AcceptedRisk, Waived, FalsePositive, Resolved }
public enum QualityEvidenceKind { SourceCode, TestResult, RuntimeMeasurement, ToolResult, Artifact, Document, HumanAttestation }
public enum QualityImportCompleteness { Complete, Partial }

public sealed record QualityTaxonomyTerm(
    string Id,
    string Axis,
    string Description,
    int Order,
    IReadOnlyList<string> Aliases,
    bool Deprecated,
    string? Replacement = null,
    IReadOnlyList<string>? AllowedAxes = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    IReadOnlyList<QualityTaxonomyTerm> Terms,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed class QualityTaxonomyCatalogue
{
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
    private const string CoreResourceSuffix = "catalogues.quality-studio-core.v1.json";
    private readonly IReadOnlyDictionary<(string Axis, string Id), QualityTaxonomyTerm> terms;

    private QualityTaxonomyCatalogue(
        QualityTaxonomyCatalogueDocument document,
        string digest)
    {
        Document = document;
        Digest = digest;
        terms = document.Terms.ToDictionary(term => (term.Axis, term.Id));
    }

    public QualityTaxonomyCatalogueDocument Document { get; }
    public string Digest { get; }

    public static QualityTaxonomyCatalogue LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The core quality taxonomy resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var document = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The core quality taxonomy must be a JSON object.");
        Validate(document);
        return new QualityTaxonomyCatalogue(
            document,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public bool TryGetTerm(string axis, string id, out QualityTaxonomyTerm? term) =>
        terms.TryGetValue((axis, id), out term);

    public string ResolveCanonicalId(string axis, string id)
    {
        if (terms.ContainsKey((axis, id))) return id;
        return Document.Terms.FirstOrDefault(term =>
            string.Equals(term.Axis, axis, StringComparison.Ordinal) &&
            term.Aliases.Contains(id, StringComparer.Ordinal))?.Id ?? id;
    }

    public bool SupportsAggregation(string aspectId, string axis)
    {
        if (!TryGetTerm("aspect", aspectId, out var term) || term is null)
            return false;
        return term.AllowedAxes?.Contains(axis, StringComparer.Ordinal) == true;
    }

    public QualityTaxonomyReference CreateReference() =>
        new(Document.Id, Document.Version, Digest);

    private static void Validate(QualityTaxonomyCatalogueDocument document)
    {
        if (document.SchemaVersion != 1 ||
            !string.Equals(document.Id, CoreId, StringComparison.Ordinal) ||
            !string.Equals(document.Version, CoreVersion, StringComparison.Ordinal))
            throw new JsonException("The embedded core quality taxonomy identity is invalid.");

        var duplicate = document.Terms
            .GroupBy(term => (term.Axis, term.Id))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new JsonException($"Duplicate taxonomy term '{duplicate.Key.Axis}/{duplicate.Key.Id}'.");
    }
}

public sealed record QualityTaxonomyReference(
    string Id,
    string Version,
    string Digest,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationSubject(
    string UnitId,
    string ManifestHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProducer(
    QualityProducerKind Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    string ReviewRunId,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    string? ArtifactUri = null,
    int? Line = null,
    int? Column = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityEvidence(
    string Id,
    QualityEvidenceKind Kind,
    QualityEvidenceLocator Locator,
    string Summary,
    string? ContentHash = null,
    string? MediaType = null,
    string? RawContent = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(
    int Score,
    GradeBand Band,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationAspect(
    string AspectId,
    string Rationale,
    QualityAssessment? Assessment = null,
    QualityChange? Change = null,
    QualityObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationDecision(
    QualityDecision Value,
    string PolicyRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityFindingSource(
    QualityProducerKind Kind,
    string ProducerRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    IReadOnlyList<string>? FingerprintAliases = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationLegacy(
    string Schema,
    string Value,
    string SourcePath,
    QualityImportCompleteness Completeness,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string ObservationId { get; init; }

    [JsonPropertyOrder(3)]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonPropertyOrder(4)]
    public required QualityTaxonomyReference Taxonomy { get; init; }

    [JsonPropertyOrder(5)]
    public IReadOnlyList<QualityTaxonomyReference> ExtensionTaxonomies { get; init; } = [];

    [JsonPropertyOrder(6)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(9)]
    public required QualityEvidenceStatus EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<QualityEvidence> Evidence { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<QualityObservationAspect> Aspects { get; init; }

    [JsonPropertyOrder(12)]
    public required QualityAssessment Assessment { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationDecision? Decision { get; init; }

    [JsonPropertyOrder(14)]
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationLegacy? Legacy { get; init; }

    [JsonPropertyOrder(16)]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();

    [JsonExtensionData]
    public IDictionary<string, JsonElement> LegacyExtensions { get; init; } = new Dictionary<string, JsonElement>();
}

public enum QualityObservationReadStatus { Supported, UnsupportedSchemaMajor, UnsupportedTaxonomyMajor }

public sealed record QualityObservationReadResult(
    QualityObservationReadStatus Status,
    string RawJson,
    QualityObservationDocument? Observation);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument document)
    {
        ValidateSupported(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static QualityObservationReadResult Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("A quality observation must be a JSON object.");
        if (!parsed.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
            !schemaVersionElement.TryGetInt32(out var schemaVersion))
            throw new JsonException("A quality observation requires an integer schemaVersion.");
        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
            return new(QualityObservationReadStatus.UnsupportedSchemaMajor, json, null);

        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(json, Options)
            ?? throw new JsonException("A quality observation must be a JSON object.");
        if (!string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schema '{observation.Schema}'.");
        if (!TryReadSemVerMajor(observation.Taxonomy.Version, out var taxonomyMajor))
            throw new JsonException("taxonomy.version must be Semantic Versioning.");
        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomyCatalogue.CoreId, StringComparison.Ordinal) ||
            taxonomyMajor != 1)
            return new(QualityObservationReadStatus.UnsupportedTaxonomyMajor, json, null);

        ValidateSupported(observation);
        return new(QualityObservationReadStatus.Supported, json, observation);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new QualityUtcTimestampConverter());
        options.Converters.Add(new QualityGradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void ValidateSupported(QualityObservationDocument document)
    {
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{document.SchemaVersion}'.");
        if (document.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");
        if (!string.Equals(document.Taxonomy.Id, QualityTaxonomyCatalogue.CoreId, StringComparison.Ordinal) ||
            !TryReadSemVerMajor(document.Taxonomy.Version, out var major) || major != 1)
            throw new JsonException($"Unsupported core taxonomy '{document.Taxonomy.Id}@{document.Taxonomy.Version}'.");
        if (document.Decision is not null && string.IsNullOrWhiteSpace(document.Decision.PolicyRef))
            throw new JsonException("A policy decision requires policyRef.");
        if (document.Aspects.Any(aspect => (aspect.Assessment is null) == (aspect.Change is null)))
            throw new JsonException("Each aspect must carry exactly one assessment or change value.");
        if (document.LegacyExtensions.Keys.Any(key => !key.StartsWith("x-", StringComparison.Ordinal)))
            throw new JsonException("Legacy root extensions must use an x-* key.");
        var evidenceIds = document.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (document.Findings.SelectMany(finding => finding.EvidenceRefs).Any(reference => !evidenceIds.Contains(reference)))
            throw new JsonException("Every finding evidence reference must resolve within its observation.");
    }

    private static bool TryReadSemVerMajor(string version, out int major)
    {
        major = 0;
        var separator = version.IndexOf('.');
        return separator > 0 && int.TryParse(version.AsSpan(0, separator), out major);
    }

    private sealed class QualityGradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class QualityUtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new JsonException("Quality observation timestamps must be UTC instants ending in Z.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero)
                throw new JsonException("Quality observation timestamps must be UTC instants.");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}

public sealed record LegacyQualityProjection(
    QualityAssessment? Assessment = null,
    QualityChange? Change = null,
    QualityDecision? Decision = null,
    QualityEvidenceStatus? EvidenceStatus = null,
    QualityLifecycleState? Lifecycle = null,
    string? PolicyRef = null,
    string? LegacyValue = null);

public static class LegacyQualityMapper
{
    public const string SecurityPolicyRef = "security-sensor-agent@1";

    public static LegacyQualityProjection MapSecurityVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass, Decision: QualityDecision.Allow,
            EvidenceStatus: QualityEvidenceStatus.Available, PolicyRef: SecurityPolicyRef, LegacyValue: value),
        "warn" => new(QualityAssessment.Concern, Decision: QualityDecision.Warn,
            EvidenceStatus: QualityEvidenceStatus.Available, PolicyRef: SecurityPolicyRef, LegacyValue: value),
        "block" => new(QualityAssessment.Fail, Decision: QualityDecision.Block,
            EvidenceStatus: QualityEvidenceStatus.Available, PolicyRef: SecurityPolicyRef, LegacyValue: value),
        "unavailable" => new(QualityAssessment.Inconclusive, Decision: QualityDecision.Defer,
            EvidenceStatus: QualityEvidenceStatus.Unavailable, PolicyRef: SecurityPolicyRef, LegacyValue: value),
        _ => throw Unknown("security verdict", value),
    };

    public static LegacyQualityProjection MapFlowVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass, LegacyValue: value),
        "fail" => new(QualityAssessment.Fail, LegacyValue: value),
        "undetermined" => new(QualityAssessment.Inconclusive, LegacyValue: value),
        _ => throw Unknown("flow verdict", value),
    };

    public static LegacyQualityProjection MapAttackVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass, LegacyValue: value),
        "finding" => new(QualityAssessment.Fail, LegacyValue: value),
        "not-applicable" => new(QualityAssessment.NotApplicable, LegacyValue: value),
        "not-yet-checked" => new(QualityAssessment.NotAssessed, LegacyValue: value),
        _ => throw Unknown("attack verdict", value),
    };

    public static LegacyQualityProjection MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => new(Change: QualityChange.NoObservedDelta, LegacyValue: value),
        "improved" => new(Change: QualityChange.Improved, LegacyValue: value),
        "neutral" => new(Change: QualityChange.Unchanged, LegacyValue: value),
        "regression" => new(Change: QualityChange.Regressed, LegacyValue: value),
        _ => throw Unknown("change summary", value),
    };

    public static LegacyQualityProjection MapChangeAspect(string value) => value switch
    {
        "good" => new(QualityAssessment.Pass, LegacyValue: value),
        "mixed" => new(QualityAssessment.Concern, LegacyValue: value),
        "concerning" => new(QualityAssessment.Fail, LegacyValue: value),
        "unknown" => new(QualityAssessment.Inconclusive, LegacyValue: value),
        _ => throw Unknown("change aspect", value),
    };

    public static LegacyQualityProjection MapFindingState(string value) => value switch
    {
        "open" => new(Lifecycle: QualityLifecycleState.Open, LegacyValue: value),
        "accepted" => new(Lifecycle: QualityLifecycleState.AcceptedRisk, LegacyValue: value),
        "waived" => new(Lifecycle: QualityLifecycleState.Waived, LegacyValue: value),
        "falsePositive" or "false-positive" => new(Lifecycle: QualityLifecycleState.FalsePositive, LegacyValue: value),
        "resolved" => new(Lifecycle: QualityLifecycleState.Resolved, LegacyValue: value),
        _ => throw Unknown("finding state", value),
    };

    public static QualityEvidence MapEvidenceString(string value, string evidenceId = "legacy-evidence")
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var json = false;
        try
        {
            using var ignored = JsonDocument.Parse(value);
            json = true;
        }
        catch (JsonException)
        {
            // Legacy text is evidence too; preserve it byte-for-byte instead of rejecting it.
        }

        return new QualityEvidence(
            evidenceId,
            json ? QualityEvidenceKind.ToolResult : QualityEvidenceKind.Document,
            new QualityEvidenceLocator(),
            json ? "Preserved legacy JSON evidence." : "Preserved legacy text evidence.",
            digest,
            json ? "application/json" : "text/plain",
            value);
    }

    private static ArgumentOutOfRangeException Unknown(string contract, string value) =>
        new(nameof(value), value, $"Unknown legacy {contract} value.");
}

