using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Computes the stable operation identity carried into archived operation, finding and usage records.
/// The id is derived only from the run id and the operand path, so it stays stable across recovery of
/// that operation regardless of which stopped attempt eventually completes it.
/// </summary>
public static class ReviewOperationId
{
    public const string AggregateOperand = "@aggregate";

    public static string ForFile(string runId, string path) => Compute(runId, path);

    public static string ForAggregate(string runId) => Compute(runId, AggregateOperand);

    private static string Compute(string runId, string operand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operand);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(runId + "\u0000" + operand));
        return "op_" + Convert.ToHexStringLower(digest)[..24];
    }
}

/// <summary>
/// Immutable identity, plan and configuration for one archived review run.
/// Create-only: <see cref="ReviewRunArchiveStore.CreateRun"/> never overwrites an existing file.
/// </summary>
public sealed record RunArchiveRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required string RepositoryId { get; init; }

    [JsonPropertyOrder(4)]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyOrder(5)]
    public required RunArchiveNode Node { get; init; }

    [JsonPropertyOrder(6)]
    public required string Level { get; init; }

    [JsonPropertyOrder(7)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyOrder(9)]
    public required string CliType { get; init; }

    [JsonPropertyOrder(10)]
    public required IReadOnlyList<RunArchiveTarget> Targets { get; init; }

    [JsonPropertyOrder(11)]
    public bool Force { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThinkingLevel { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TokenCap { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostCap { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRevision { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SourceDirty { get; init; }
}

public sealed record RunArchiveNode(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] string Path);

public sealed record RunArchiveTarget(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] string Path,
    [property: JsonPropertyOrder(3)] string SubjectHash);

/// <summary>
/// Append-only record of one completed or failed operation. Appended to <c>operations.jsonl</c>
/// only after the operation's result sidecar is atomically written, or after a terminal error is known.
/// </summary>
public sealed record RunArchiveOperation
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(3)]
    public required int Ordinal { get; init; }

    [JsonPropertyOrder(4)]
    public required int Attempt { get; init; }

    [JsonPropertyOrder(5)]
    public required string UnitId { get; init; }

    [JsonPropertyOrder(6)]
    public required string Path { get; init; }

    [JsonPropertyOrder(7)]
    public required string Level { get; init; }

    [JsonPropertyOrder(8)]
    public required string State { get; init; }

    [JsonPropertyOrder(9)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyOrder(10)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderRunId { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewedHash { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SidecarPath { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GradeScore { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradeBand? GradeBand { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict { get; init; }

    [JsonPropertyOrder(17), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// Append-only record of what one operation observed, including the finding's lifecycle state at
/// observation time. Never the current lifecycle owner; <c>.quality/findings/state.json</c> remains that.
/// </summary>
public sealed record RunArchiveFinding
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(3)]
    public required string Fingerprint { get; init; }

    [JsonPropertyOrder(4)]
    public required string FindingId { get; init; }

    [JsonPropertyOrder(5)]
    public required string RuleId { get; init; }

    [JsonPropertyOrder(6)]
    public required FindingSeverity Severity { get; init; }

    [JsonPropertyOrder(7)]
    public required string Title { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<FindingLocation> Locations { get; init; }

    [JsonPropertyOrder(9)]
    public required string State { get; init; }
}

/// <summary>
/// Create-only snapshot of one stopped attempt of a review run. A capped run that resumes adds
/// another attempt at the next ordinal; earlier attempts are never rewritten.
/// </summary>
public sealed record RunArchiveAttempt
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required int Attempt { get; init; }

    [JsonPropertyOrder(4)]
    public required string Outcome { get; init; }

    [JsonPropertyOrder(5)]
    public required string Completeness { get; init; }

    [JsonPropertyOrder(6)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyOrder(7)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyOrder(8)]
    public required int TotalFiles { get; init; }

    [JsonPropertyOrder(9)]
    public required int CompletedFiles { get; init; }

    [JsonPropertyOrder(10)]
    public required int FailedFiles { get; init; }

    [JsonPropertyOrder(11)]
    public required int SkippedFiles { get; init; }

    [JsonPropertyOrder(12)]
    public required TokenUsage Usage { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostSpent { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyOrder(15)]
    public required string PriceStatus { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    [JsonPropertyOrder(17)]
    public IReadOnlyList<string> UsageLedgerMonths { get; init; } = [];

    [JsonPropertyOrder(18)]
    public required DateTimeOffset ArchivedAt { get; init; }
}

public sealed record StoredRunArchive(
    RunArchiveRecord Run,
    IReadOnlyList<RunArchiveOperation> Operations,
    IReadOnlyList<RunArchiveFinding> Findings,
    IReadOnlyList<RunArchiveAttempt> Attempts);

public static class RunArchiveJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    public static JsonSerializerOptions LineOptions { get; } = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
        };
        options.Converters.Add(new UtcTimestampConverter());
        options.Converters.Add(new NullableUtcTimestampConverter());
        options.Converters.Add(new GradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class GradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("gradeBand must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new JsonException("Archive timestamps must be UTC ISO 8601 values.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }

    private sealed class NullableUtcTimestampConverter : JsonConverter<DateTimeOffset?>
    {
        public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            var value = reader.GetString();
            if (value is null || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new JsonException("Archive timestamps must be UTC ISO 8601 values.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            else
                writer.WriteNullValue();
        }
    }
}
