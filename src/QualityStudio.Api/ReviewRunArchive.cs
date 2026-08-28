using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveNode(string UnitId, string Name, string Path);

public sealed record RunArchiveTarget(string UnitId, string Name, string Path, string SubjectHash);

public sealed record RunArchiveEstimate(
    int Files,
    int Operations,
    long PromptCharacters,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    string PriceStatus,
    int HistorySamples,
    string Method);

/// <summary>Immutable, create-only identity and plan for one review run. Written once when the run is archived.</summary>
public sealed record RunArchiveRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string RepositoryId,
    [property: JsonPropertyOrder(4)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(5)] RunArchiveNode Node,
    [property: JsonPropertyOrder(6)] string? Level,
    [property: JsonPropertyOrder(7)] string Kind,
    [property: JsonPropertyOrder(8)] string? Model,
    [property: JsonPropertyOrder(9)] string? ThinkingLevel,
    [property: JsonPropertyOrder(10)] string CliType,
    [property: JsonPropertyOrder(11)] bool Force,
    [property: JsonPropertyOrder(12)] IReadOnlyList<RunArchiveTarget> Targets,
    [property: JsonPropertyOrder(13)] IReadOnlyList<string>? AggregateControls = null,
    [property: JsonPropertyOrder(14)] long? TokenCap = null,
    [property: JsonPropertyOrder(15)] decimal? CostCap = null,
    [property: JsonPropertyOrder(16)] RunArchiveEstimate? Estimate = null,
    [property: JsonPropertyOrder(17)] string? SourceRevision = null,
    [property: JsonPropertyOrder(18)] bool? SourceDirty = null)
{
    public const string SchemaUri = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public enum RunVerdictKind
{
    Grade,
    Security,
    None,
}

public sealed record RunArchiveGrade(int Score, string Band);

/// <summary>
/// Security verdicts are carried as their existing lowercase wire values ("pass"/"warn"/"block"/"unavailable")
/// rather than the <see cref="SecurityVerdict"/> enum type, so this contract does not depend on that enum's
/// otherwise-unconverted default integer serialization.
/// </summary>
public sealed record RunArchiveVerdict(RunVerdictKind Kind, RunArchiveGrade? Grade = null, string? SecurityVerdict = null);

/// <summary>Append-only record of one completed, failed, or skipped review operation within an attempt.</summary>
public sealed record RunOperationRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] int Attempt,
    [property: JsonPropertyOrder(5)] int Ordinal,
    [property: JsonPropertyOrder(6)] string UnitId,
    [property: JsonPropertyOrder(7)] string Path,
    [property: JsonPropertyOrder(8)] string? Level,
    [property: JsonPropertyOrder(9)] string Kind,
    [property: JsonPropertyOrder(10)] string State,
    [property: JsonPropertyOrder(11)] string SubjectHash,
    [property: JsonPropertyOrder(12)] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyOrder(13)] DateTimeOffset? FinishedAt = null,
    [property: JsonPropertyOrder(14)] string? ProviderRunId = null,
    [property: JsonPropertyOrder(15)] string? ReviewInputsHash = null,
    [property: JsonPropertyOrder(16)] string? ResultSidecarPath = null,
    [property: JsonPropertyOrder(17)] DateTimeOffset? ReviewedAt = null,
    [property: JsonPropertyOrder(18)] RunArchiveVerdict? Verdict = null,
    [property: JsonPropertyOrder(19)] string? Error = null)
{
    public const string SchemaUri = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record RunFindingLocation(string Path, int? StartLine = null, int? EndLine = null);

/// <summary>Append-only observation of one finding's identity and lifecycle state as seen by one operation.</summary>
public sealed record RunFindingRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] string Fingerprint,
    [property: JsonPropertyOrder(5)] string FindingId,
    [property: JsonPropertyOrder(6)] string RuleId,
    [property: JsonPropertyOrder(7)] string Severity,
    [property: JsonPropertyOrder(8)] string Title,
    [property: JsonPropertyOrder(9)] string State,
    [property: JsonPropertyOrder(10)] DateTimeOffset ObservedAt,
    [property: JsonPropertyOrder(11)] IReadOnlyList<RunFindingLocation>? Locations = null)
{
    public const string SchemaUri = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record RunAttemptCounters(int TotalFiles, int CompletedFiles, int FailedFiles, int SkippedFiles);

public sealed record RunAttemptEstimateDeviation(decimal InputTokensPercent, decimal OutputTokensPercent, decimal? CostPercent);

public sealed record RunAttemptQualitySummary(
    RunArchiveGrade? LowestGrade,
    string? WorstSecurityVerdict,
    int ActiveFindingCount,
    string? HighestActiveSeverity);

/// <summary>
/// Create-only snapshot of one stopped attempt of a run. A capped run that resumes gets a second,
/// separately numbered attempt rather than rewriting this one.
/// </summary>
public sealed record RunAttemptRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] int AttemptNumber,
    [property: JsonPropertyOrder(4)] string Outcome,
    [property: JsonPropertyOrder(5)] string Completeness,
    [property: JsonPropertyOrder(6)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset ArchivedAt,
    [property: JsonPropertyOrder(8)] RunAttemptCounters Counters,
    [property: JsonPropertyOrder(9)] TokenUsage Usage,
    [property: JsonPropertyOrder(10)] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyOrder(11)] DateTimeOffset? FinishedAt = null,
    [property: JsonPropertyOrder(12)] decimal? CostSpent = null,
    [property: JsonPropertyOrder(13)] string? Currency = null,
    [property: JsonPropertyOrder(14)] string? PriceStatus = null,
    [property: JsonPropertyOrder(15)] string? StopReason = null,
    [property: JsonPropertyOrder(16)] IReadOnlyList<string>? ErrorCodes = null,
    [property: JsonPropertyOrder(17)] RunAttemptEstimateDeviation? EstimateDeviation = null,
    [property: JsonPropertyOrder(18)] IReadOnlyList<string>? LedgerMonths = null,
    [property: JsonPropertyOrder(19)] IReadOnlyList<string>? OperationIds = null,
    [property: JsonPropertyOrder(20)] RunAttemptQualitySummary? QualitySummary = null)
{
    public const string SchemaUri = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

/// <summary>A run's identity plus every attempt archived for it so far, ordered oldest attempt first.</summary>
public sealed record StoredRunArchive(
    RunArchiveRecord Run,
    IReadOnlyList<RunAttemptRecord> Attempts,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings)
{
    public RunAttemptRecord? LatestAttempt => Attempts.Count == 0 ? null : Attempts[^1];
}
