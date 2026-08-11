using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ReviewMetaDocument
{
    public const int CurrentSchemaVersion = 3;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/review-meta.v3.schema.json";
    public const int PreviousSchemaVersion = 2;
    public const string PreviousSchemaId = "https://agent-orchestrator.dev/quality/schemas/review-meta.v2.schema.json";
    public const int LegacySchemaVersion = 1;
    public const string LegacySchemaId = "https://agent-orchestrator.dev/quality/schemas/review-meta.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required ReviewUnit Unit { get; init; }

    [JsonPropertyOrder(3)]
    public required DateTimeOffset ReviewedAt { get; init; }

    [JsonPropertyOrder(4)]
    public required ReviewKind Kind { get; init; }

    [JsonPropertyOrder(5)]
    public required ReviewerIdentity Reviewer { get; init; }

    [JsonPropertyOrder(6)]
    public required ManifestHash ReviewedHash { get; init; }

    [JsonPropertyOrder(7)]
    public required IReadOnlyList<SubjectInputHash> SubjectInputs { get; init; }

    [JsonPropertyOrder(8)]
    public required ReviewInputs ReviewInputs { get; init; }

    [JsonPropertyOrder(9)]
    public required ReviewGrade Grade { get; init; }

    [JsonPropertyOrder(10)]
    public required string Summary { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<ReviewAspect> Aspects { get; init; }

    [JsonPropertyOrder(12)]
    public required IReadOnlyList<ReviewFinding> Findings { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewAggregate? Aggregate { get; init; }

    [JsonPropertyOrder(14)]
    public IReadOnlyList<ReviewThread> Threads { get; init; } = [];

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityReviewMetadata? Security { get; init; }

    [JsonPropertyOrder(16)]
    public IReadOnlyList<SensorScanResult> DeterministicEvidence { get; init; } = [];

    [JsonPropertyOrder(17), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewOrigin? Origin { get; init; }
}

public sealed record ReviewUnit(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] ReviewAdapter Adapter,
    [property: JsonPropertyOrder(2)] ReviewLevel Level,
    [property: JsonPropertyOrder(3)] string Path,
    [property: JsonPropertyOrder(4)] string DisplayName,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null);

public enum ReviewAdapter { Angular, Dotnet, Generic }

public sealed record ReviewerIdentity(
    [property: JsonPropertyOrder(0)] string Agent,
    [property: JsonPropertyOrder(1)] string Model,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AgentVersion = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewerUsage? Usage = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ReviewerSensorReference>? Sensors = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null);

public sealed record ReviewOrigin(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId,
    [property: JsonPropertyOrder(2)] string OperationRunId,
    [property: JsonPropertyOrder(3)] ReviewRoute Requested,
    [property: JsonPropertyOrder(4)] ReviewRoute Executed,
    [property: JsonPropertyOrder(5)] PromptReference Prompt,
    [property: JsonPropertyOrder(6)] string ReviewInputHash,
    [property: JsonPropertyOrder(7)] string SubjectManifestHash,
    [property: JsonPropertyOrder(8)] string SourceRevision,
    [property: JsonPropertyOrder(9)] DateTimeOffset ObservedAt);

public sealed record ReviewRoute(
    [property: JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Cli,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel);

public sealed record ReviewerSensorReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string ResultHash);

public sealed record SecurityReviewMetadata(
    [property: JsonPropertyOrder(0)] string Verdict,
    [property: JsonPropertyOrder(1)] string CombinationRule,
    [property: JsonPropertyOrder(2)] IReadOnlyList<SecuritySensorMetadata> Sensors);

public sealed record SecuritySensorMetadata(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string ResultHash,
    [property: JsonPropertyOrder(3)] bool Available,
    [property: JsonPropertyOrder(4)] string? UnavailableReason,
    [property: JsonPropertyOrder(5)] string Verdict,
    [property: JsonPropertyOrder(6)] IReadOnlyDictionary<string, string> ToolVersions);

public sealed record ManifestHash(
    [property: JsonPropertyOrder(0)] string Algorithm,
    [property: JsonPropertyOrder(1)] string Canonicalization,
    [property: JsonPropertyOrder(2)] string Value)
{
    public static ManifestHash Subject(string value) =>
        new("sha256", "quality-studio-subject-manifest-v1", value);

    public static ManifestHash ReviewInput(string value) =>
        new("sha256", "quality-studio-review-inputs-v1", value);
}

public sealed record ReviewInputs(
    [property: JsonPropertyOrder(0)] ManifestHash EffectiveHash,
    [property: JsonPropertyOrder(1)] bool Complete,
    [property: JsonPropertyOrder(2)] IReadOnlyList<StandardReference> Standards,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> Omitted,
    [property: JsonPropertyOrder(4)] PromptReference Prompt);

public sealed record StandardReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] StandardScope Scope,
    [property: JsonPropertyOrder(2)] string Version,
    [property: JsonPropertyOrder(3)] string ContentHash);

public enum StandardScope { BuiltIn, Global, Project }

public sealed record PromptReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string ContentHash);

public sealed record ReviewGrade(
    [property: JsonPropertyOrder(0)] int Score,
    [property: JsonPropertyOrder(1)] GradeBand Band,
    [property: JsonPropertyOrder(2)] string Rationale);

public enum GradeBand { A, B, C, D, F }

public sealed record ReviewAspect(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Title,
    [property: JsonPropertyOrder(2)] ReviewGrade Grade);

public sealed record ReviewFinding(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Aspect,
    [property: JsonPropertyOrder(2)] FindingSeverity Severity,
    [property: JsonPropertyOrder(3)] string Title,
    [property: JsonPropertyOrder(4)] string Description,
    [property: JsonPropertyOrder(5)] string Recommendation,
    [property: JsonPropertyOrder(6)] IReadOnlyList<FindingLocation> Locations,
    [property: JsonPropertyOrder(7)] string Fingerprint,
    [property: JsonPropertyOrder(8)] string RuleId,
    [property: JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Evidence = null,
    [property: JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FindingSource? Source = null,
    [property: JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Impact = null,
    [property: JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FindingEvidenceItem>? EvidenceItems = null,
    [property: JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FindingReproduction? Reproduction = null);

public sealed record FindingEvidenceItem(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Class,
    [property: JsonPropertyOrder(2)] string Status,
    [property: JsonPropertyOrder(3)] string Summary,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? AnchorIndex = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Producer = null,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reference = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultHash = null);

public sealed record FindingReproduction(
    [property: JsonPropertyOrder(0)] string Status,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string> Steps,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Expected = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Observed = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonPropertyOrder(5)] IReadOnlyList<FindingReproductionAttempt>? Attempts = null);

public sealed record FindingReproductionAttempt(
    [property: JsonPropertyOrder(0)] DateTimeOffset ExecutedAt,
    [property: JsonPropertyOrder(1)] string Executor,
    [property: JsonPropertyOrder(2)] string Result,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactReference = null);

public enum FindingSeverity { Critical, High, Medium, Low, Info }

public enum FindingSourceKind { Deterministic }

public sealed record FindingSource(
    [property: JsonPropertyOrder(0)] FindingSourceKind Kind,
    [property: JsonPropertyOrder(1)] string SensorId,
    [property: JsonPropertyOrder(2)] string Producer,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProducerVersion = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RunIndex = null);

public sealed record FindingLocation(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FindingRange? Range = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SymbolId = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CapturedExcerpt? CapturedExcerpt = null);

public sealed record CapturedExcerpt(
    [property: JsonPropertyOrder(0)] string Text,
    [property: JsonPropertyOrder(1)] string Language,
    [property: JsonPropertyOrder(2)] string ContentHash,
    [property: JsonPropertyOrder(3)] string ExcerptHash);

public sealed record FindingRange(
    [property: JsonPropertyOrder(0)] FindingPosition Start,
    [property: JsonPropertyOrder(1)] FindingPosition End);

public sealed record FindingPosition(
    [property: JsonPropertyOrder(0)] int Line,
    [property: JsonPropertyOrder(1)] int Column);

public sealed record ReviewThread(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] ReviewThreadAnchor Anchor,
    [property: JsonPropertyOrder(2)] ReviewThreadStatus Status,
    [property: JsonPropertyOrder(3)] IReadOnlyList<ReviewThreadEntry> Entries,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewAnchorState? AnchorState = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? HealedAt = null);

public sealed record ReviewThreadAnchor(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] string Fingerprint,
    [property: JsonPropertyOrder(2)] string ContextHash,
    [property: JsonPropertyOrder(3)] FindingRange LastKnownRange);

public sealed record ReviewThreadEntry(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] ReviewThreadAuthor Author,
    [property: JsonPropertyOrder(2)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(3)] string Body,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReplyTo = null);

public sealed record ReviewThreadAuthor(
    [property: JsonPropertyOrder(0)] ReviewAuthorKind Kind,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null);

public enum ReviewThreadStatus { Open, Resolved }
public enum ReviewAnchorState { Anchored, Healed, Detached }
public enum ReviewAuthorKind { Agent, Human }

public sealed record ReviewAggregate(
    [property: JsonPropertyOrder(0)] IReadOnlyList<AggregateMember> Members,
    [property: JsonPropertyOrder(1)] IReadOnlyList<AggregateExclusion> Excluded);

public sealed record AggregateMember(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] string Path,
    [property: JsonPropertyOrder(2)] string SubjectHash);

public sealed record AggregateExclusion(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] string Reason);

public static class ReviewMetaJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(ReviewMetaDocument document)
    {
        ValidateContractVersion(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static ReviewMetaDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<ReviewMetaDocument>(json, Options)
            ?? throw new JsonException("Review metadata must be a JSON object.");
        ValidateContractVersion(document);
        return document;
    }

    public static async ValueTask<ReviewMetaDocument> LoadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var document = await JsonSerializer.DeserializeAsync<ReviewMetaDocument>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new JsonException("Review metadata must be a JSON object.");
        ValidateContractVersion(document);
        return document;
    }

    public static async ValueTask SaveAsync(
        Stream stream,
        ReviewMetaDocument document,
        CancellationToken cancellationToken = default)
    {
        ValidateContractVersion(document);
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new UtcTimestampConverter());
        options.Converters.Add(new GradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void ValidateContractVersion(ReviewMetaDocument document)
    {
        var current = document.SchemaVersion == ReviewMetaDocument.CurrentSchemaVersion &&
                      string.Equals(document.Schema, ReviewMetaDocument.SchemaId, StringComparison.Ordinal);
        var previous = document.SchemaVersion == ReviewMetaDocument.PreviousSchemaVersion &&
                       string.Equals(document.Schema, ReviewMetaDocument.PreviousSchemaId, StringComparison.Ordinal);
        var legacy = document.SchemaVersion == ReviewMetaDocument.LegacySchemaVersion &&
                     string.Equals(document.Schema, ReviewMetaDocument.LegacySchemaId, StringComparison.Ordinal);
        if (!current && !previous && !legacy)
        {
            throw new JsonException($"Unsupported review metadata schemaVersion '{document.SchemaVersion}'.");
        }
        if (legacy && document.Unit.Adapter == ReviewAdapter.Generic)
        {
            throw new JsonException("The generic review adapter requires review metadata schemaVersion '2'.");
        }

        if (document.ReviewedAt.Offset != TimeSpan.Zero)
        {
            throw new JsonException("reviewedAt must be a UTC instant.");
        }
        if (current)
        {
            if (document.Origin is null)
                throw new JsonException("review-meta v3 requires execution origin provenance.");
            if (document.Origin.ObservedAt.Offset != TimeSpan.Zero)
                throw new JsonException("origin.observedAt must be a UTC instant.");
            foreach (var finding in document.Findings)
            {
                if (string.IsNullOrWhiteSpace(finding.Impact) || finding.EvidenceItems is null || finding.Reproduction is null)
                    throw new JsonException("review-meta v3 findings require impact, typed evidence, and reproduction status.");
                for (var index = 0; index < finding.Locations.Count; index++)
                {
                    if (finding.Locations[index].Range is not null && finding.Locations[index].CapturedExcerpt is null)
                        throw new JsonException("review-meta v3 ranged locations require a runner-captured excerpt.");
                }
            }
        }
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') ||
                !DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                throw new JsonException("reviewedAt must use UTC ISO 8601 with millisecond precision.");
            }

            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero)
            {
                throw new JsonException("reviewedAt must be a UTC instant.");
            }

            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }

    private sealed class GradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
