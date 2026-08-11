using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public static class ReviewMetaV3
{
    public const int SchemaVersion = 3;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/review-meta.v3.schema.json";
}

public sealed record ReviewMetaV3Document
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = ReviewMetaV3.SchemaId;

    [JsonPropertyOrder(1)] public int SchemaVersion { get; init; } = ReviewMetaV3.SchemaVersion;
    [JsonPropertyOrder(2)] public required ReviewUnit Unit { get; init; }
    [JsonPropertyOrder(3)] public required DateTimeOffset ReviewedAt { get; init; }
    [JsonPropertyOrder(4)] public required ReviewKind Kind { get; init; }
    [JsonPropertyOrder(5)] public required ReviewerIdentityV3 Reviewer { get; init; }
    [JsonPropertyOrder(6)] public required ManifestHash ReviewedHash { get; init; }
    [JsonPropertyOrder(7)] public required IReadOnlyList<SubjectInputHash> SubjectInputs { get; init; }
    [JsonPropertyOrder(8)] public required ReviewInputs ReviewInputs { get; init; }
    [JsonPropertyOrder(9)] public required ReviewGrade Grade { get; init; }
    [JsonPropertyOrder(10)] public required string Summary { get; init; }
    [JsonPropertyOrder(11)] public required IReadOnlyList<ReviewAspect> Aspects { get; init; }
    [JsonPropertyOrder(12)] public required IReadOnlyList<ReviewFindingV3> Findings { get; init; }
    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ReviewAggregate? Aggregate { get; init; }
    [JsonPropertyOrder(14)] public IReadOnlyList<ReviewThread> Threads { get; init; } = [];
    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SecurityReviewMetadata? Security { get; init; }
    [JsonPropertyOrder(16)] public IReadOnlyList<SensorScanResult> DeterministicEvidence { get; init; } = [];
}

public sealed record ReviewerIdentityV3(
    string Agent,
    string Model,
    string RunId,
    ReviewerUsage Usage,
    ReviewRoute Requested,
    ExecutedReviewRoute Executed,
    IReadOnlyList<ReviewerSensorReference>? Sensors = null);

public sealed record ReviewRoute(string? Model, string? ThinkingLevel);
public sealed record ExecutedReviewRoute(string Cli, string Model, string? ThinkingLevel);

public sealed record ReviewFindingV3(
    string Id,
    string Fingerprint,
    string Aspect,
    FindingSeverity Severity,
    string Title,
    string Problem,
    string Impact,
    string Remediation,
    string RuleId,
    IReadOnlyList<ReviewAnchor> Anchors,
    IReadOnlyList<FindingEvidence> Evidence,
    FindingReproduction Reproduction,
    FindingOrigin Origin,
    IReadOnlyList<FindingLocation> Locations,
    string Description,
    string Recommendation,
    FindingSource? Source = null);

public sealed record ReviewAnchor(
    string Id,
    string Role,
    string Path,
    FindingRange Range,
    CapturedExcerpt CapturedExcerpt,
    string? SymbolId = null);

public sealed record CapturedExcerpt(string Text, string Language, string ContentHash, string ExcerptHash);

public sealed record FindingEvidence(
    string Id,
    [property: JsonPropertyName("class")] string Class,
    string Status,
    string Summary,
    string? AnchorId = null,
    string? Reference = null);

public sealed record FindingReproduction(
    string Status,
    IReadOnlyList<string> Steps,
    string? Expected = null,
    string? Observed = null,
    string? Reason = null,
    IReadOnlyList<ReproductionAttempt>? Attempts = null);

public sealed record ReproductionAttempt(
    DateTimeOffset AttemptedAt,
    string Executor,
    string Result,
    string? ArtifactReference = null);

public sealed record FindingOrigin(
    string Kind,
    string? ReviewRunId,
    string OperationRunId,
    ReviewRoute Requested,
    ExecutedReviewRoute Executed,
    PromptReference Prompt,
    string ReviewInputHash,
    string SubjectManifestHash,
    string SourceRevision,
    DateTimeOffset ObservedAt);

public static class ReviewMetaV3Json
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(ReviewMetaV3Document document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static ReviewMetaV3Document Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<ReviewMetaV3Document>(json, Options)
            ?? throw new JsonException("Review metadata must be a JSON object.");
        Validate(document);
        return document;
    }

    private static void Validate(ReviewMetaV3Document document)
    {
        if (document.SchemaVersion != ReviewMetaV3.SchemaVersion ||
            !string.Equals(document.Schema, ReviewMetaV3.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported review metadata schemaVersion '{document.SchemaVersion}'.");
        if (document.ReviewedAt.Offset != TimeSpan.Zero) throw new JsonException("reviewedAt must be a UTC instant.");
        foreach (var finding in document.Findings)
        {
            if (finding.Anchors.Count == 0 || finding.Evidence.All(evidence => evidence.Class != "source-span"))
                throw new JsonException("Every v3 file finding requires an anchor and runner-captured source-span evidence.");
            if (finding.Origin.Kind == "agent" &&
                (finding.Reproduction.Status == "verified" || finding.Evidence.Any(evidence =>
                    evidence.Class is "deterministic-result" or "test-result" or "runtime-observation")))
                throw new JsonException("Agent findings cannot claim executor or deterministic provenance.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcConverter());
        return options;
    }

    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
                throw new JsonException("Timestamp must be UTC ISO 8601.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }
}
