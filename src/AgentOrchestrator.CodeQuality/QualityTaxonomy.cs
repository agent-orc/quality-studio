using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed class QualityTaxonomyOptions
{
    public const string SectionName = "QualityTaxonomy";

    public bool ObservationWriteEnabled { get; set; }

    public bool ObservationReadEnabled { get; set; }
}

public sealed record QualityTaxonomyTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? Replacement = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityAspectTerm(
    string Id,
    string Title,
    string Description,
    int Order,
    IReadOnlyList<string> AllowedAxes,
    IReadOnlyList<string>? Aliases = null,
    bool Deprecated = false,
    string? Replacement = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityTaxonomyAxes(
    IReadOnlyList<QualityTaxonomyTerm> ProducerKind,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceStatus,
    IReadOnlyList<QualityTaxonomyTerm> Assessment,
    IReadOnlyList<QualityTaxonomyTerm> Change,
    IReadOnlyList<QualityTaxonomyTerm> Decision,
    IReadOnlyList<QualityTaxonomyTerm> Severity,
    IReadOnlyList<QualityTaxonomyTerm> Lifecycle,
    IReadOnlyList<QualityTaxonomyTerm> EvidenceKind);

public sealed record QualityTaxonomyDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    string Prefix,
    QualityTaxonomyAxes Axes,
    IReadOnlyList<QualityAspectTerm> Aspects,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityCatalogueReference(string Id, string Version, string Digest);

public sealed record QualityObservationSubject(
    string UnitId,
    string ManifestHash,
    string Scope,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProfile(
    string Id,
    string Version,
    string PromptHash,
    string ReviewInputsHash,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationProducer(
    string Kind,
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
    int? Line = null,
    int? Column = null,
    string? ArtifactRef = null,
    string? Uri = null);

public sealed record QualityEvidence(
    string Id,
    string Kind,
    QualityEvidenceLocator Locator,
    string Summary,
    string? ContentHash = null,
    string? MediaType = null,
    JsonElement? Raw = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationGrade(int Score, string Band);

public sealed record QualityObservationAspect(
    string AspectId,
    string Assessment,
    string Rationale,
    string? Change = null,
    QualityObservationGrade? Grade = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationDecision(string Value, string PolicyRef);

public sealed record QualityFindingSource(
    string Kind,
    string ProducerRef,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationFinding(
    string ObservationFindingId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    string RuleRef,
    string AspectId,
    string Severity,
    IReadOnlyList<string> EvidenceRefs,
    QualityFindingSource Source,
    IReadOnlyList<string>? FingerprintAliases = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

public sealed record QualityObservationLegacy(
    string Schema,
    string SourcePath,
    string Completeness,
    IReadOnlyDictionary<string, string> Values,
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
    public required QualityCatalogueReference Taxonomy { get; init; }

    [JsonPropertyOrder(5)]
    public IReadOnlyList<QualityCatalogueReference> ExtensionCatalogues { get; init; } = [];

    [JsonPropertyOrder(6)]
    public required QualityObservationSubject Subject { get; init; }

    [JsonPropertyOrder(7)]
    public required QualityObservationProfile Profile { get; init; }

    [JsonPropertyOrder(8)]
    public required QualityObservationProducer Producer { get; init; }

    [JsonPropertyOrder(9)]
    public required string EvidenceStatus { get; init; }

    [JsonPropertyOrder(10)]
    public IReadOnlyList<QualityEvidence> Evidence { get; init; } = [];

    [JsonPropertyOrder(11)]
    public IReadOnlyList<QualityObservationAspect> Aspects { get; init; } = [];

    [JsonPropertyOrder(12)]
    public required string Assessment { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Change { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationDecision? Decision { get; init; }

    [JsonPropertyOrder(15)]
    public IReadOnlyList<QualityObservationFinding> Findings { get; init; } = [];

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QualityObservationLegacy? Legacy { get; init; }

    [JsonPropertyOrder(17)]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public enum QualityObservationSupport
{
    Supported,
    UnsupportedSchemaMajor,
    UnsupportedTaxonomyMajor,
}

public sealed record QualityObservationReadResult(
    QualityObservationSupport Support,
    JsonElement Raw,
    QualityObservationDocument? Observation,
    string? Reason)
{
    public bool IsSupported => Support == QualityObservationSupport.Supported;
}

public static class QualityTaxonomyCatalogue
{
    public const string CoreId = "quality-studio/core";
    public const string CoreVersion = "1.0.0";
    private const string CoreResourceSuffix = "catalogues.quality-studio-core.v1.json";
    private static readonly Lazy<(QualityTaxonomyDocument Document, string Digest)> Core = new(LoadCore);

    public static QualityTaxonomyDocument CoreDocument => Core.Value.Document;
    public static string CoreDigest => Core.Value.Digest;
    public static QualityCatalogueReference CoreReference => new(CoreId, CoreVersion, CoreDigest);

    public static bool IsInstalledAspect(string aspectId, IEnumerable<QualityTaxonomyDocument> installedCatalogues) =>
        installedCatalogues.Any(catalogue =>
        {
            if (string.Equals(catalogue.Id, CoreId, StringComparison.Ordinal))
            {
                return CoreDocument.Aspects.Any(aspect =>
                    string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));
            }

            return aspectId.StartsWith(catalogue.Prefix + ":", StringComparison.Ordinal) &&
                   catalogue.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal));
        });

    public static QualityCatalogueReference? SourceCatalogue(
        QualityObservationDocument observation,
        string aspectId)
    {
        if (CoreDocument.Aspects.Any(aspect => string.Equals(aspect.Id, aspectId, StringComparison.Ordinal)))
            return observation.Taxonomy;

        var separator = aspectId.IndexOf(':');
        if (separator <= 0) return null;
        var prefix = aspectId[..separator];
        return observation.ExtensionCatalogues.FirstOrDefault(catalogue =>
            string.Equals(catalogue.Id, prefix, StringComparison.Ordinal) ||
            catalogue.Id.StartsWith(prefix + "/", StringComparison.Ordinal));
    }

    public static IReadOnlyList<QualityObservationAspect> SelectInstalledAspects(
        QualityObservationDocument observation,
        IEnumerable<QualityTaxonomyDocument> installedCatalogues) =>
        observation.Aspects.Where(aspect => IsInstalledAspect(aspect.AspectId, installedCatalogues)).ToArray();

    private static (QualityTaxonomyDocument Document, string Digest) LoadCore()
    {
        var assembly = typeof(QualityTaxonomyCatalogue).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded taxonomy catalogue '{resource}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var document = JsonSerializer.Deserialize<QualityTaxonomyDocument>(bytes, QualityObservationJson.Options)
            ?? throw new JsonException("The core taxonomy catalogue must be a JSON object.");
        if (document.SchemaVersion != 1 ||
            !string.Equals(document.Id, CoreId, StringComparison.Ordinal) ||
            !string.Equals(document.Version, CoreVersion, StringComparison.Ordinal))
        {
            throw new JsonException("The embedded core taxonomy catalogue identity is invalid.");
        }

        return (document, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}

public static class QualityObservationJson
{
    private static readonly HashSet<string> ProducerKinds =
        ["agent", "deterministic-sensor", "human", "imported", "unknown"];
    private static readonly HashSet<string> EvidenceStatuses =
        ["available", "partial", "unavailable"];
    private static readonly HashSet<string> Assessments =
        ["pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed"];
    private static readonly HashSet<string> Changes =
        ["improved", "regressed", "mixed", "unchanged", "no-observed-delta", "inconclusive"];
    private static readonly HashSet<string> Decisions =
        ["allow", "warn", "block", "defer"];
    private static readonly HashSet<string> EvidenceKinds =
        ["source-code", "test-result", "runtime-measurement", "tool-result", "artifact", "document", "human-attestation"];
    private static readonly HashSet<string> Severities =
        ["critical", "high", "medium", "low", "info"];
    private static readonly HashSet<string> GradeBands = ["A", "B", "C", "D", "F"];

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityObservationDocument observation)
    {
        ValidateSupported(observation);
        return JsonSerializer.Serialize(observation, Options);
    }

    public static QualityObservationReadResult ReadPreservingUnsupported(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var raw = parsed.RootElement.Clone();
        if (raw.ValueKind != JsonValueKind.Object)
            throw new JsonException("Quality observation must be a JSON object.");

        var schemaVersion = raw.TryGetProperty("schemaVersion", out var schemaVersionElement) &&
                            schemaVersionElement.TryGetInt32(out var parsedVersion)
            ? parsedVersion
            : throw new JsonException("Quality observation requires an integer schemaVersion.");
        if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
        {
            return new QualityObservationReadResult(
                QualityObservationSupport.UnsupportedSchemaMajor,
                raw,
                null,
                $"Unsupported quality observation schemaVersion '{schemaVersion}'.");
        }

        if (!raw.TryGetProperty("taxonomy", out var taxonomy) || taxonomy.ValueKind != JsonValueKind.Object ||
            !taxonomy.TryGetProperty("version", out var taxonomyVersionElement))
            throw new JsonException("Quality observation requires taxonomy.version.");
        var taxonomyVersion = taxonomyVersionElement.GetString() ?? string.Empty;
        if (!TryGetSemVerMajor(taxonomyVersion, out var taxonomyMajor))
            throw new JsonException($"Invalid taxonomy version '{taxonomyVersion}'.");
        if (taxonomyMajor != 1)
        {
            return new QualityObservationReadResult(
                QualityObservationSupport.UnsupportedTaxonomyMajor,
                raw,
                null,
                $"Unsupported taxonomy major '{taxonomyMajor}'.");
        }

        var normalized = NormalizeLegacyRootExtensions(raw);
        var observation = JsonSerializer.Deserialize<QualityObservationDocument>(normalized, Options)
            ?? throw new JsonException("Quality observation must be a JSON object.");
        ValidateSupported(observation);
        return new QualityObservationReadResult(QualityObservationSupport.Supported, raw, observation, null);
    }

    private static JsonElement NormalizeLegacyRootExtensions(JsonElement raw)
    {
        var legacyExtensions = raw.EnumerateObject()
            .Where(property => property.Name.StartsWith("x-", StringComparison.Ordinal))
            .ToArray();
        if (legacyExtensions.Length == 0) return raw;

        var root = JsonNode.Parse(raw.GetRawText())?.AsObject()
            ?? throw new JsonException("Quality observation must be a JSON object.");
        var extensions = root["extensions"]?.AsObject()
            ?? throw new JsonException("Quality observation requires an extensions object.");
        foreach (var legacyExtension in legacyExtensions)
        {
            if (extensions.ContainsKey(legacyExtension.Name))
            {
                throw new JsonException(
                    $"Legacy root extension '{legacyExtension.Name}' conflicts with extensions.{legacyExtension.Name}.");
            }

            extensions[legacyExtension.Name] = JsonNode.Parse(legacyExtension.Value.GetRawText());
            root.Remove(legacyExtension.Name);
        }

        return JsonSerializer.SerializeToElement(root, Options);
    }

    public static string HashContent(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void ValidateSupported(QualityObservationDocument observation)
    {
        if (observation.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(observation.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality observation schemaVersion '{observation.SchemaVersion}'.");
        if (observation.Taxonomy is null || observation.Subject is null || observation.Profile is null ||
            observation.Producer is null || observation.Evidence is null || observation.Aspects is null ||
            observation.Findings is null || observation.ExtensionCatalogues is null || observation.Extensions is null)
            throw new JsonException("Quality observation is missing a required object or collection.");
        if (!string.Equals(observation.Taxonomy.Id, QualityTaxonomyCatalogue.CoreId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported core taxonomy '{observation.Taxonomy.Id}'.");
        if (!TryGetSemVerMajor(observation.Taxonomy.Version, out var major) || major != 1)
            throw new JsonException($"Unsupported taxonomy version '{observation.Taxonomy.Version}'.");
        if (!IsTaggedSha256(observation.ObservationId, "observation-sha256:") ||
            !IsTaggedSha256(observation.Taxonomy.Digest, "sha256:") ||
            observation.ExtensionCatalogues.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                !TryGetSemVerMajor(item.Version, out _) ||
                !IsTaggedSha256(item.Digest, "sha256:")) ||
            !IsTaggedSha256(observation.Subject.ManifestHash, "sha256:") ||
            !IsTaggedSha256(observation.Profile.PromptHash, "sha256:") ||
            !IsTaggedSha256(observation.Profile.ReviewInputsHash, "sha256:"))
            throw new JsonException("Quality observation identity and catalogue, subject, and profile hashes must be SHA-256 values.");
        if (observation.ObservedAt.Offset != TimeSpan.Zero)
            throw new JsonException("observedAt must be a UTC instant.");
        if (!ProducerKinds.Contains(observation.Producer.Kind) ||
            !EvidenceStatuses.Contains(observation.EvidenceStatus) ||
            !Assessments.Contains(observation.Assessment) ||
            observation.Change is not null && !Changes.Contains(observation.Change) ||
            observation.Decision is not null &&
            (!Decisions.Contains(observation.Decision.Value) || string.IsNullOrWhiteSpace(observation.Decision.PolicyRef)))
            throw new JsonException("Quality observation contains an unsupported semantic axis value.");
        var evidenceIds = observation.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (evidenceIds.Count != observation.Evidence.Count)
            throw new JsonException("Quality observation evidence ids must be unique.");
        if (observation.Evidence.Any(item =>
                string.IsNullOrWhiteSpace(item.Id) ||
                !EvidenceKinds.Contains(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Summary) ||
                item.ContentHash is not null && !IsTaggedSha256(item.ContentHash, "sha256:") ||
                item.Locator is null ||
                item.Locator.Path is null && item.Locator.SymbolId is null && item.Locator.ArtifactRef is null &&
                item.Locator.Uri is null && item.Locator.Line is null && item.Locator.Column is null))
            throw new JsonException("Quality observation contains invalid typed evidence.");
        if (observation.Aspects.Any(item =>
                string.IsNullOrWhiteSpace(item.AspectId) ||
                !Assessments.Contains(item.Assessment) ||
                string.IsNullOrWhiteSpace(item.Rationale) ||
                item.Change is not null && !Changes.Contains(item.Change) ||
                item.Grade is not null &&
                (item.Grade.Score is < 0 or > 100 || !GradeBands.Contains(item.Grade.Band))))
            throw new JsonException("Quality observation contains an invalid aspect assessment or grade.");
        foreach (var finding in observation.Findings)
        {
            if (finding is null)
                throw new JsonException("Quality observation findings cannot contain null entries.");
            if (!Severities.Contains(finding.Severity) ||
                !ProducerKinds.Contains(finding.Source.Kind) ||
                string.IsNullOrWhiteSpace(finding.Source.ProducerRef))
                throw new JsonException($"Finding '{finding.ObservationFindingId}' has invalid severity or producer provenance.");
            if (finding.EvidenceRefs is not { Count: > 0 } ||
                finding.EvidenceRefs.Any(reference => !evidenceIds.Contains(reference)))
                throw new JsonException($"Finding '{finding.ObservationFindingId}' has an unresolved evidence reference.");
        }
    }

    private static bool TryGetSemVerMajor(string version, out int major)
    {
        var separator = version.IndexOf('.');
        major = 0;
        return separator > 0 && int.TryParse(version.AsSpan(0, separator), out major);
    }

    private static bool IsTaggedSha256(string? value, string prefix) =>
        value is not null && value.Length == prefix.Length + 64 &&
        value.StartsWith(prefix, StringComparison.Ordinal) &&
        value[prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

public enum LegacyQualityVocabulary
{
    SecurityVerdict,
    FlowVerdict,
    AttackVerdict,
    ChangeSummary,
    ChangeAspect,
    FindingState,
}

public sealed record LegacyQualityMapping(
    string? Assessment = null,
    string? EvidenceStatus = null,
    string? Change = null,
    string? Decision = null,
    string? PolicyRef = null,
    string? Lifecycle = null,
    string? LegacyValue = null);

public static class QualityLegacyMapper
{
    public const string SecurityCombinationPolicy = "security-sensor-agent-v1";
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtensions =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public static LegacyQualityMapping Map(LegacyQualityVocabulary vocabulary, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return (vocabulary, value) switch
        {
            (LegacyQualityVocabulary.SecurityVerdict, "pass") => new(
                "pass", "available", Decision: "allow", PolicyRef: SecurityCombinationPolicy, LegacyValue: value),
            (LegacyQualityVocabulary.SecurityVerdict, "warn") => new(
                "concern", "available", Decision: "warn", PolicyRef: SecurityCombinationPolicy, LegacyValue: value),
            (LegacyQualityVocabulary.SecurityVerdict, "block") => new(
                "fail", "available", Decision: "block", PolicyRef: SecurityCombinationPolicy, LegacyValue: value),
            (LegacyQualityVocabulary.SecurityVerdict, "unavailable") => new(
                "inconclusive", "unavailable", PolicyRef: SecurityCombinationPolicy, LegacyValue: value),

            (LegacyQualityVocabulary.FlowVerdict, "pass") => new("pass", LegacyValue: value),
            (LegacyQualityVocabulary.FlowVerdict, "fail") => new("fail", LegacyValue: value),
            (LegacyQualityVocabulary.FlowVerdict, "undetermined") => new("inconclusive", LegacyValue: value),

            (LegacyQualityVocabulary.AttackVerdict, "pass") => new("pass", LegacyValue: value),
            (LegacyQualityVocabulary.AttackVerdict, "finding") => new("fail", LegacyValue: value),
            (LegacyQualityVocabulary.AttackVerdict, "not-applicable") => new("not-applicable", LegacyValue: value),
            (LegacyQualityVocabulary.AttackVerdict, "not-yet-checked") => new("not-assessed", LegacyValue: value),

            (LegacyQualityVocabulary.ChangeSummary, "no-quality-delta") => new(Change: "no-observed-delta", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeSummary, "improved") => new(Change: "improved", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeSummary, "neutral") => new(Change: "unchanged", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeSummary, "regression") => new(Change: "regressed", LegacyValue: value),

            (LegacyQualityVocabulary.ChangeAspect, "good") => new("pass", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeAspect, "mixed") => new("concern", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeAspect, "concerning") => new("fail", LegacyValue: value),
            (LegacyQualityVocabulary.ChangeAspect, "unknown") => new("inconclusive", LegacyValue: value),

            (LegacyQualityVocabulary.FindingState, "open") => new(Lifecycle: "open", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "accepted") => new(Lifecycle: "accepted-risk", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "accepted-risk") => new(Lifecycle: "accepted-risk", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "waived") => new(Lifecycle: "waived", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "falsePositive") => new(Lifecycle: "false-positive", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "false-positive") => new(Lifecycle: "false-positive", LegacyValue: value),
            (LegacyQualityVocabulary.FindingState, "resolved") => new(Lifecycle: "resolved", LegacyValue: value),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Unsupported {vocabulary} legacy value '{value}'."),
        };
    }

    public static QualityEvidence MapEvidence(string id, string value, QualityEvidenceLocator locator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(locator);

        try
        {
            using var document = JsonDocument.Parse(value);
            return new QualityEvidence(
                id,
                "tool-result",
                locator,
                "Structured legacy evidence.",
                QualityObservationJson.HashContent(value),
                "application/json",
                document.RootElement.Clone(),
                NoExtensions);
        }
        catch (JsonException)
        {
            return new QualityEvidence(
                id,
                "document",
                locator,
                value,
                QualityObservationJson.HashContent(value),
                "text/plain",
                JsonSerializer.SerializeToElement(value),
                NoExtensions);
        }
    }
}
