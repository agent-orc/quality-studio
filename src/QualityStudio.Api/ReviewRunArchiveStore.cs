using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QualityStudio.Api;

public sealed record RunRecordNode(string Id, string Name, string Path);

public sealed record RunRecordTarget(string Id, string Name, string Path, string SubjectHash);

public sealed record RunRecordEstimate(
    int Files, int Operations, long InputTokens, long OutputTokens, decimal? Cost, string? Currency,
    int HistorySamples, string Method);

public sealed record RunRecordSourceRevision(string Commit, bool Dirty);

/// <summary>Immutable, create-only run identity and plan. Written once at <c>run.json</c>.</summary>
public sealed record RunRecord(
    string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    RunRecordNode Node,
    string Level,
    string Kind,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    IReadOnlyList<RunRecordTarget> Targets,
    long? TokenCap,
    decimal? CostCap,
    RunRecordEstimate? Estimate,
    RunRecordSourceRevision? SourceRevision)
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public int SchemaVersion { get; init; } = 1;
}

public sealed record RunOperationGrade(int Score, string Band, string Rationale);

public sealed record RunOperationVerdict(string Kind, RunOperationGrade? Grade, string? SecurityVerdict);

/// <summary>One append-only line per completed or failed operation, at <c>operations.jsonl</c>.</summary>
public sealed record RunOperationRecord(
    string RunId,
    string OperationId,
    int Ordinal,
    int Attempt,
    string UnitId,
    string Path,
    string Level,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ProviderRunId,
    string? ReviewedHash,
    string? InputHash,
    string? ResultSidecarPath,
    RunOperationVerdict? Verdict)
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public int SchemaVersion { get; init; } = 1;
}

public sealed record RunFindingLocation(string Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

/// <summary>
/// One append-only line per finding observed by an operation, at <c>findings.jsonl</c>. This is
/// never the current lifecycle owner; <c>.quality/findings/state.json</c> remains that.
/// </summary>
public sealed record RunFindingRecord(
    string RunId,
    string OperationId,
    DateTimeOffset ObservedAt,
    string Fingerprint,
    string Id,
    string RuleId,
    string Severity,
    string Title,
    string State,
    IReadOnlyList<RunFindingLocation> Locations)
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public int SchemaVersion { get; init; } = 1;
}

public sealed record RunAttemptCounters(int Total, int Completed, int Failed, int Skipped);

public sealed record RunAttemptSpend(
    int Operations, long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningOutputTokens,
    long DurationMs, decimal? Cost, string? Currency, string PriceStatus);

public sealed record RunAttemptCap(long? TokenLimit, decimal? CostLimit, string Outcome, string? Reason);

public sealed record RunAttemptEstimateDeviation(decimal? InputPercent, decimal? OutputPercent, decimal? CostPercent);

public sealed record RunAttemptFindingCounts(
    int Total, IReadOnlyDictionary<string, int> BySeverity, IReadOnlyDictionary<string, int> ByState);

public sealed record RunAttemptQualitySummary(
    int? Score, string? Grade, RunAttemptFindingCounts Findings, string? HighestSeverity, string? PartialReason);

/// <summary>
/// One create-only snapshot per stopped attempt, at <c>attempts/NNNN.json</c>. A capped run that
/// resumes gets another attempt; earlier attempts are never rewritten.
/// </summary>
public sealed record RunAttemptRecord(
    string RunId,
    int Attempt,
    string Outcome,
    string Completeness,
    DateTimeOffset? StartedAt,
    DateTimeOffset FinishedAt,
    RunAttemptCounters AttemptCounters,
    RunAttemptCounters CumulativeCounters,
    RunAttemptSpend AttemptSpend,
    RunAttemptSpend CumulativeSpend,
    IReadOnlyList<string> ErrorCodes,
    RunAttemptCap Cap,
    RunAttemptEstimateDeviation? EstimateDeviation,
    IReadOnlyList<string> LedgerReferences,
    RunAttemptQualitySummary QualitySummary)
{
    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
/// Persists the tracked, immutable run-history archive beside the mutable <see cref="ReviewRunStore"/>
/// recovery journal. Every document is create-only or append-only; nothing here is ever rewritten in
/// place, matching the run-persistence dossier's durability contract.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    public void CreateRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
    }

    public RunRecord LoadRun(string runId, DateTimeOffset createdAt)
    {
        var path = Path.Combine(RunDirectory(runId, createdAt), "run.json");
        return ReadRequired<RunRecord>(path);
    }

    public void AppendOperation(string runId, DateTimeOffset createdAt, RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        AppendLine(Path.Combine(RunDirectory(runId, createdAt), "operations.jsonl"), operation);
    }

    public IReadOnlyList<RunOperationRecord> LoadOperations(string runId, DateTimeOffset createdAt) =>
        ReadLines<RunOperationRecord>(Path.Combine(RunDirectory(runId, createdAt), "operations.jsonl"));

    public void AppendFinding(string runId, DateTimeOffset createdAt, RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        AppendLine(Path.Combine(RunDirectory(runId, createdAt), "findings.jsonl"), finding);
    }

    public IReadOnlyList<RunFindingRecord> LoadFindings(string runId, DateTimeOffset createdAt) =>
        ReadLines<RunFindingRecord>(Path.Combine(RunDirectory(runId, createdAt), "findings.jsonl"));

    public void CreateAttempt(string runId, DateTimeOffset createdAt, RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentException("An attempt ordinal must be 1 or greater.", nameof(attempt));
        var directory = Path.Combine(RunDirectory(runId, createdAt), "attempts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{attempt.Attempt:D4}.json");
        WriteCreateOnly(path, JsonSerializer.Serialize(attempt, JsonOptions) + Environment.NewLine);
    }

    public IReadOnlyList<RunAttemptRecord> LoadAttempts(string runId, DateTimeOffset createdAt)
    {
        var directory = Path.Combine(RunDirectory(runId, createdAt), "attempts");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(ReadRequired<RunAttemptRecord>)
            .OrderBy(attempt => attempt.Attempt)
            .ToArray();
    }

    private static void AppendLine<T>(string path, T record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(JsonSerializer.Serialize(record, LineJsonOptions) + "\n");
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != '\n')
            {
                stream.Position = stream.Length;
                stream.WriteByte((byte)'\n');
            }
        }
        stream.Position = stream.Length;
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    private static IReadOnlyList<T> ReadLines<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return records;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(archiveRoot, month, runId);
    }

    private static T ReadRequired<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Review run archive file is empty: {path}");

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
