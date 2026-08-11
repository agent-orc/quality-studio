using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum QualityProducerKind { Agent, DeterministicSensor, Human, Imported, Unknown }
public enum QualityEvidenceStatus { Available, Partial, Unavailable }
public enum QualityAssessment { Pass, Concern, Fail, Inconclusive, NotApplicable, NotAssessed }
public enum QualityChange { Improved, Regressed, Mixed, Unchanged, NoObservedDelta, Inconclusive }
public enum QualityDecisionValue { Allow, Warn, Block, Defer }
public enum QualityLifecycleState { Open, AcceptedRisk, Waived, FalsePositive, Resolved }
public enum QualityEvidenceKind { SourceCode, TestResult, RuntimeMeasurement, ToolResult, Artifact, Document, HumanAttestation }

public sealed record QualityTaxonomyTerm(
    string Id,
    string Description,
    int Order,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? ReplacedBy = null);

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string>? Aliases,
    bool Deprecated,
    string? ReplacedBy,
    IReadOnlyList<string> AllowedAxes);

public sealed record QualityTaxonomyAxes(
    IReadOnlyList<QualityTaxonomyTerm> ProducerKind,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceStatus,
    IReadOnlyList<QualityTaxonomyTerm> Assessment,
    IReadOnlyList<QualityTaxonomyTerm> Change,
    IReadOnlyList<QualityTaxonomyTerm> Decision,
    IReadOnlyList<QualityTaxonomyTerm> Severity,
    IReadOnlyList<QualityTaxonomyTerm> Lifecycle,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceKind);

public sealed record QualityTaxonomyCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    QualityTaxonomyAxes Axes,
    IReadOnlyList<QualityAspectTerm> Aspects);

public static class QualityTaxonomyCatalogue
{
    public const string SchemaId = "https://quality.studio/schemas/quality-taxonomy.v1.schema.json";
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
    private const string ResourceSuffix = "catalogues.quality-studio-core.v1.json";
    private static readonly Lazy<(QualityTaxonomyCatalogueDocument Document, string Digest)> Core = new(LoadCore);

    public static QualityTaxonomyCatalogueDocument CoreDocument => Core.Value.Document;
    public static string CoreDigest => Core.Value.Digest;

    public static bool IsCoreAspect(string id) =>
        CoreDocument.Aspects.Any(term => string.Equals(term.Id, id, StringComparison.Ordinal));

    public static string? ResolveAspect(string legacyOrCanonicalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyOrCanonicalId);
        return CoreDocument.Aspects.FirstOrDefault(term =>
            string.Equals(term.Id, legacyOrCanonicalId, StringComparison.Ordinal) ||
            (term.Aliases?.Contains(legacyOrCanonicalId, StringComparer.Ordinal) ?? false))?.Id;
    }

    private static (QualityTaxonomyCatalogueDocument, string) LoadCore()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(resource =>
            resource.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The core quality taxonomy resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var document = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The core quality taxonomy must be an object.");
        if (document.SchemaVersion != 1 || document.Schema != SchemaId ||
            document.Id != CoreId || document.Version != CoreVersion)
            throw new JsonException("The embedded core quality taxonomy has an unsupported identity.");
        return (document, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

public sealed record QualityCatalogueReference(string Id, string Version, string Digest);

public sealed record QualitySubject(
    string UnitId,
    string ManifestHash,
    string Scope,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    string Kind,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityProducer(
    QualityProducerKind Kind,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    string RunId,
    string? ReviewRunId,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityEvidenceLocator(
    string? Path = null,
    string? SymbolId = null,
    int? StartLine = null,
    int? StartColumn = null,
    int? EndLine = null,
    int? EndColumn = null,
    string? Reference = null);

public sealed record QualityEvidence(
    string Id,
    QualityEvidenceKind Kind,
    QualityEvidenceLocator Locator,
    string Summary,
    string? ContentHash,
    string? MediaType,
    JsonElement? Raw,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityGrade(int Score, string Band);

public sealed record QualityAspectObservation(
    string AspectId,
    string? Title,
    QualityAssessment? Assessment,
    QualityChange? Change,
    string Rationale,
    QualityGrade? Grade,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityPolicyDecision(QualityDecisionValue Value, string PolicyRef);
public sealed record QualityFindingSource(QualityProducerKind Kind, string ProducerRef);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    IReadOnlyList<string> FingerprintAliases,
    string RuleRef,
    string AspectId,
    FindingSeverity Severity,
    string Title,
    string Description,
    string Recommendation,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityLegacyReference(
    string Schema,
    JsonElement? Value,
    string SourcePath,
    string Completeness,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public sealed record QualityObservation(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string ObservationId,
    DateTimeOffset ObservedAt,
    QualityCatalogueReference Taxonomy,
    IReadOnlyList<QualityCatalogueReference>? ExtensionCatalogues,
    QualitySubject Subject,
    QualityProfile Profile,
    QualityProducer Producer,
    QualityEvidenceStatus EvidenceStatus,
    IReadOnlyList<QualityEvidence> Evidence,
    IReadOnlyList<QualityAspectObservation> Aspects,
    QualityAssessment Assessment,
    QualityChange? Change,
    QualityPolicyDecision? Decision,
    IReadOnlyList<QualityObservationFinding> Findings,
    QualityLegacyReference? Legacy,
    IReadOnlyDictionary<string, JsonElement> Extensions)
{
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public enum QualityObservationSupport { Supported, UnsupportedSchema, UnsupportedTaxonomy }

public sealed record QualityObservationReadResult(
    QualityObservationSupport Support,
    QualityObservation? Observation,
    JsonElement Raw,
    string? Reason);

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtensionsValue =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, JsonElement> NoExtensions => NoExtensionsValue;

    public static string Serialize(QualityObservation observation)
    {
        Validate(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationReadResult Read(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("A quality observation must be an object.");
        if (!raw.TryGetProperty("schemaVersion", out var schemaVersion) || schemaVersion.GetInt32() != 1 ||
            !raw.TryGetProperty("$schema", out var schema) || schema.GetString() != QualityObservation.SchemaId)
            return new(QualityObservationSupport.UnsupportedSchema, null, raw,
                "The observation schema major is unsupported.");
        if (!raw.TryGetProperty("taxonomy", out var taxonomy) || taxonomy.ValueKind != JsonValueKind.Object ||
            !taxonomy.TryGetProperty("version", out var versionNode) || versionNode.ValueKind != JsonValueKind.String)
            throw new JsonException("A supported observation requires a taxonomy version.");
        var taxonomyVersion = versionNode.GetString();
        if (!TryMajor(taxonomyVersion, out var major) || major != 1)
            return new(QualityObservationSupport.UnsupportedTaxonomy, null, raw,
                $"Taxonomy version '{taxonomyVersion ?? "unknown"}' is structured but unsupported.");
        var observation = JsonSerializer.Deserialize<QualityObservation>(raw, Options)
            ?? throw new JsonException("A quality observation must be an object.");
        Validate(observation);
        return new(QualityObservationSupport.Supported, observation, raw, null);
    }

    public static QualityObservation Deserialize(string json)
    {
        var result = Read(json);
        return result.Observation ?? throw new JsonException(result.Reason);
    }

    public static void Validate(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Taxonomy is null || observation.Subject is null || observation.Profile is null ||
            observation.Producer is null || observation.Evidence is null || observation.Aspects is null ||
            observation.Findings is null || observation.Extensions is null)
            throw new JsonException("The observation is missing a required object or collection.");
        if (observation.SchemaVersion != QualityObservation.CurrentSchemaVersion || observation.Schema != QualityObservation.SchemaId)
            throw new JsonException("The observation schema major is unsupported.");
        if (!TryMajor(observation.Taxonomy.Version, out var major) || major != 1)
            throw new JsonException($"Taxonomy version '{observation.Taxonomy.Version}' is unsupported.");
        if (observation.Taxonomy.Id == QualityTaxonomyCatalogue.CoreId &&
            (observation.Taxonomy.Version != QualityTaxonomyCatalogue.CoreVersion ||
             observation.Taxonomy.Digest != QualityTaxonomyCatalogue.CoreDigest))
            throw new JsonException("The core taxonomy reference does not match the installed catalogue.");
        if (!observation.ObservationId.StartsWith("observation-sha256:", StringComparison.Ordinal) ||
            observation.ObservationId.Length != "observation-sha256:".Length + 64)
            throw new JsonException("observationId must be a content-derived identifier.");
        if (string.IsNullOrWhiteSpace(observation.Producer.Provider) ||
            string.IsNullOrWhiteSpace(observation.Producer.RequestedModel) ||
            string.IsNullOrWhiteSpace(observation.Producer.EffectiveModel) ||
            string.IsNullOrWhiteSpace(observation.Producer.ThinkingLevel) ||
            string.IsNullOrWhiteSpace(observation.Producer.RoutePolicyVersion))
            throw new JsonException("Producer provenance must be explicit; use 'unknown' when it is unavailable.");
        if (observation.Decision is not null && string.IsNullOrWhiteSpace(observation.Decision.PolicyRef))
            throw new JsonException("A policy decision requires policyRef.");
        var evidenceIds = observation.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (evidenceIds.Count != observation.Evidence.Count)
            throw new JsonException("Evidence ids must be unique within an observation.");
        if (observation.Evidence.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Summary) ||
                string.IsNullOrWhiteSpace(item.Locator.Path) &&
                string.IsNullOrWhiteSpace(item.Locator.SymbolId) &&
                string.IsNullOrWhiteSpace(item.Locator.Reference)))
            throw new JsonException("Every evidence item requires an id, summary, and locator or artifact reference.");
        foreach (var finding in observation.Findings)
        {
            if (string.IsNullOrWhiteSpace(finding.Source.ProducerRef) ||
                finding.EvidenceRefs.Any(reference => !evidenceIds.Contains(reference)))
                throw new JsonException($"Finding '{finding.ObservationFindingId}' contains an unresolved evidence reference.");
        }
        foreach (var aspect in observation.Aspects)
        {
            if ((aspect.Assessment is null) == (aspect.Change is null))
                throw new JsonException($"Aspect '{aspect.AspectId}' must use exactly one assessment axis.");
            var core = QualityTaxonomyCatalogue.CoreDocument.Aspects.FirstOrDefault(item => item.Id == aspect.AspectId);
            var axis = aspect.Assessment is null ? "change" : "assessment";
            if (core is not null && !core.AllowedAxes.Contains(axis, StringComparer.Ordinal))
                throw new JsonException($"Core aspect '{aspect.AspectId}' does not allow the '{axis}' axis.");
        }
    }

    public static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool TryMajor(string? version, out int major)
    {
        major = 0;
        var separator = version?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        return separator > 0 && int.TryParse(version![..separator], out major);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed record LegacySecurityMapping(
    QualityAssessment Assessment,
    QualityEvidenceStatus EvidenceStatus,
    QualityPolicyDecision? Decision);

public static class QualityLegacyMappings
{
    private static readonly IReadOnlyDictionary<string, string> AspectAliases =
        QualityTaxonomyCatalogue.CoreDocument.Aspects
            .SelectMany(term => (term.Aliases ?? []).Select(alias => KeyValuePair.Create(alias, term.Id)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static string MapAspect(string legacyId, string? producerNamespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyId);
        if (QualityTaxonomyCatalogue.IsCoreAspect(legacyId)) return legacyId;
        if (AspectAliases.TryGetValue(legacyId, out var mapped)) return mapped;
        if (legacyId == "sensor-availability")
            throw new ArgumentException("sensor-availability maps to evidenceStatus, not an aspect.", nameof(legacyId));
        var prefix = string.IsNullOrWhiteSpace(producerNamespace) ? "quality-studio" : producerNamespace.Trim();
        return $"{prefix}:producer.{legacyId}";
    }

    public static LegacySecurityMapping MapSecurityVerdict(string value) => value switch
    {
        "pass" => new(QualityAssessment.Pass, QualityEvidenceStatus.Available,
            new QualityPolicyDecision(QualityDecisionValue.Allow, "security-sensor-agent-v1")),
        "warn" => new(QualityAssessment.Concern, QualityEvidenceStatus.Available,
            new QualityPolicyDecision(QualityDecisionValue.Warn, "security-sensor-agent-v1")),
        "block" => new(QualityAssessment.Fail, QualityEvidenceStatus.Available,
            new QualityPolicyDecision(QualityDecisionValue.Block, "security-sensor-agent-v1")),
        "unavailable" => new(QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable,
            new QualityPolicyDecision(QualityDecisionValue.Defer, "security-sensor-agent-v1")),
        _ => throw Unknown("security verdict", value),
    };

    public static QualityAssessment MapFlowVerdict(string value) => value switch
    {
        "pass" => QualityAssessment.Pass,
        "fail" => QualityAssessment.Fail,
        "undetermined" => QualityAssessment.Inconclusive,
        _ => throw Unknown("flow verdict", value),
    };

    public static QualityAssessment MapAttackVerdict(string value) => value switch
    {
        "pass" => QualityAssessment.Pass,
        "finding" => QualityAssessment.Fail,
        "not-applicable" => QualityAssessment.NotApplicable,
        "not-yet-checked" => QualityAssessment.NotAssessed,
        _ => throw Unknown("attack verdict", value),
    };

    public static QualityChange MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => QualityChange.NoObservedDelta,
        "improved" => QualityChange.Improved,
        "neutral" => QualityChange.Unchanged,
        "regression" => QualityChange.Regressed,
        _ => throw Unknown("change summary", value),
    };

    public static QualityAssessment MapChangeAspect(string value) => value switch
    {
        "good" => QualityAssessment.Pass,
        "mixed" => QualityAssessment.Concern,
        "concerning" => QualityAssessment.Fail,
        "unknown" => QualityAssessment.Inconclusive,
        _ => throw Unknown("change aspect", value),
    };

    public static QualityLifecycleState MapLifecycle(string value) => value switch
    {
        "open" => QualityLifecycleState.Open,
        "accepted" or "accepted-risk" => QualityLifecycleState.AcceptedRisk,
        "waived" => QualityLifecycleState.Waived,
        "falsePositive" or "false-positive" => QualityLifecycleState.FalsePositive,
        "resolved" => QualityLifecycleState.Resolved,
        _ => throw Unknown("finding lifecycle", value),
    };

    public static QualityAssessment MapGrade(string band) => band switch
    {
        "A" or "B" => QualityAssessment.Pass,
        "C" or "D" => QualityAssessment.Concern,
        "F" => QualityAssessment.Fail,
        _ => throw Unknown("grade band", band),
    };

    public static QualityEvidence MapEvidenceString(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        JsonElement raw;
        QualityEvidenceKind kind;
        string mediaType;
        string summary;
        try
        {
            using var document = JsonDocument.Parse(value);
            raw = document.RootElement.Clone();
            kind = QualityEvidenceKind.ToolResult;
            mediaType = "application/json";
            summary = "Preserved legacy structured evidence.";
        }
        catch (JsonException)
        {
            raw = JsonSerializer.SerializeToElement(value);
            kind = QualityEvidenceKind.Document;
            mediaType = "text/plain";
            summary = value.Length <= 240 ? value : value[..240];
            if (string.IsNullOrWhiteSpace(summary)) summary = "Preserved empty legacy evidence.";
        }
        return new QualityEvidence(id, kind, new QualityEvidenceLocator(Reference: "legacy:inline"), summary,
            QualityObservationJson.Hash(value), mediaType, raw, QualityObservationJson.NoExtensions);
    }

    private static ArgumentException Unknown(string axis, string value) =>
        new($"Unknown legacy {axis} '{value}'.", nameof(value));
}
