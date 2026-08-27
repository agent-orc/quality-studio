using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QualityStudio.Api;

public sealed record ArchivedRunNode(string Id, string Name, string Path);

public sealed record ArchivedRunTarget(string UnitId, string Name, string Path, string SubjectHash);

public sealed record ArchivedRunEstimate(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    int HistorySamples,
    string Method);

/// <summary>Create-only archive of a run's identity, plan and configuration; never rewritten.</summary>
public sealed record ArchivedRunRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string RepositoryId,
    [property: JsonPropertyOrder(4)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(5)] ArchivedRunNode Node,
    [property: JsonPropertyOrder(6)] string Level,
    [property: JsonPropertyOrder(7)] string Kind,
    [property: JsonPropertyOrder(8)] IReadOnlyList<ArchivedRunTarget> Targets,
    [property: JsonPropertyOrder(9)] string? Model,
    [property: JsonPropertyOrder(10)] string? ThinkingLevel,
    [property: JsonPropertyOrder(11)] string CliType,
    [property: JsonPropertyOrder(12)] bool Force,
    [property: JsonPropertyOrder(13)] long? TokenCap,
    [property: JsonPropertyOrder(14)] decimal? CostCap,
    [property: JsonPropertyOrder(15)] ArchivedRunEstimate? Estimate,
    [property: JsonPropertyOrder(16)] string? SourceRevision,
    [property: JsonPropertyOrder(17)] bool? SourceDirty);

public sealed record ArchivedRunGrade(int Score, string Band, string Rationale);

/// <summary>One append-only record of a completed, failed, cancelled or skipped operation.</summary>
public sealed record ArchivedRunOperation(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] int Ordinal,
    [property: JsonPropertyOrder(5)] int Attempt,
    [property: JsonPropertyOrder(6)] string UnitId,
    [property: JsonPropertyOrder(7)] string Path,
    [property: JsonPropertyOrder(8)] string Level,
    [property: JsonPropertyOrder(9)] string State,
    [property: JsonPropertyOrder(10)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(11)] DateTimeOffset? FinishedAt,
    [property: JsonPropertyOrder(12)] string? ProviderRunId,
    [property: JsonPropertyOrder(13)] string? SubjectHash,
    [property: JsonPropertyOrder(14)] string? ReviewedHash,
    [property: JsonPropertyOrder(15)] string? SidecarPath,
    [property: JsonPropertyOrder(16)] string? SidecarSha256,
    [property: JsonPropertyOrder(17)] ArchivedRunGrade? Grade,
    [property: JsonPropertyOrder(18)] string? SecurityVerdict);

/// <summary>One append-only finding observation captured by the operation that produced it.</summary>
public sealed record ArchivedRunFindingLocation(string Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

public sealed record ArchivedRunFinding(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] string FindingId,
    [property: JsonPropertyOrder(5)] string RuleId,
    [property: JsonPropertyOrder(6)] string Severity,
    [property: JsonPropertyOrder(7)] string Title,
    [property: JsonPropertyOrder(8)] string Fingerprint,
    [property: JsonPropertyOrder(9)] IReadOnlyList<ArchivedRunFindingLocation> Locations,
    [property: JsonPropertyOrder(10)] string State,
    [property: JsonPropertyOrder(11)] DateTimeOffset ObservedAt);

public sealed record ArchivedAttemptCounters(int TotalFiles, int CompletedFiles, int FailedFiles, int SkippedFiles);

public sealed record ArchivedAttemptUsage(
    int Operations,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ArchivedAttemptSummary(
    string? LowestGrade,
    string? WorstSecurityVerdict,
    int ActiveFindingCount,
    string? HighestActiveSeverity);

/// <summary>Create-only snapshot of one stopped attempt (done/failed/cancelled/capped) of a run.</summary>
public sealed record ArchivedRunAttempt(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] int Attempt,
    [property: JsonPropertyOrder(4)] string Outcome,
    [property: JsonPropertyOrder(5)] string Completeness,
    [property: JsonPropertyOrder(6)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset FinishedAt,
    [property: JsonPropertyOrder(8)] ArchivedAttemptCounters Counters,
    [property: JsonPropertyOrder(9)] ArchivedAttemptUsage Usage,
    [property: JsonPropertyOrder(10)] IReadOnlyList<string> Errors,
    [property: JsonPropertyOrder(11)] string? StopReason,
    [property: JsonPropertyOrder(12)] IReadOnlyList<string> LedgerMonths,
    [property: JsonPropertyOrder(13)] ArchivedAttemptSummary Summary);

/// <summary>A full archived run as read back from <see cref="ReviewRunArchiveStore"/>.</summary>
public sealed record ArchivedReviewRun(
    ArchivedRunRecord Record,
    IReadOnlyList<ArchivedRunAttempt> Attempts,
    IReadOnlyList<ArchivedRunOperation> Operations,
    IReadOnlyList<ArchivedRunFinding> Findings);

public static class ReviewRunArchiveJson
{
    public const string RunRecordSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string RunOperationSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string RunFindingSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string RunAttemptSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static JsonSerializerOptions LineOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
