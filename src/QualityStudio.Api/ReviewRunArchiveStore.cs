using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Immutable identity, plan and configuration for one archived review run. Written once as
/// <c>.quality/run-history/YYYY-MM/&lt;runId&gt;/run.json</c>.
/// </summary>
public sealed record RunArchiveRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    string RepositoryId,
    ReviewRunPlanNode Node,
    string Level,
    string Kind,
    string Model,
    string ThinkingLevel,
    string CliType,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    bool Force,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunEstimate? Estimate,
    ReviewModelRecommendation? Recommendation,
    bool RouteOverride,
    string? SourceRevision,
    bool? SourceDirty)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
}

/// <summary>
/// One append-only line in <c>operations.jsonl</c> for a completed or failed review operation.
/// Appended only after the sidecar result is persisted, or once a terminal operation error is known.
/// </summary>
public sealed record RunOperationRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string OperationId,
    string RunId,
    int Attempt,
    int Ordinal,
    string UnitId,
    string Path,
    string Level,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? ProviderRunId,
    string? SubjectHash,
    string? ReviewedHash,
    string? SidecarPath,
    QualityRunGrade? Grade,
    string? SecurityVerdict,
    string? Error)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
}

/// <summary>
/// One append-only line in <c>findings.jsonl</c> recording what an operation observed, including the
/// finding's lifecycle state at observation time. Never becomes the current lifecycle owner.
/// </summary>
public sealed record RunFindingRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string RunId,
    string OperationId,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<QualityFindingLocation> Locations,
    string State,
    DateTimeOffset ObservedAt)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
}

public sealed record RunAttemptEstimateDeviation(
    decimal InputTokensPercent,
    decimal OutputTokensPercent,
    decimal? CostPercent,
    string Note);

public sealed record RunAttemptQualitySummary(
    int? LowestGradeScore,
    string? LowestGradeBand,
    string? WorstSecurityVerdict,
    int? ActiveFindingCount,
    string? HighestActiveSeverity);

/// <summary>
/// Create-only snapshot of one stopped attempt, written to <c>attempts/NNNN.json</c>. A logical run may
/// have more than one attempt; a later resume adds another attempt and never rewrites an earlier one.
/// </summary>
public sealed record RunAttemptRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string RunId,
    int Attempt,
    string Outcome,
    string Completeness,
    DateTimeOffset? StartedAt,
    DateTimeOffset FinishedAt,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    TokenUsage AttemptUsage,
    TokenUsage CumulativeUsage,
    decimal? AttemptCostSpent,
    decimal? CumulativeCostSpent,
    string? Currency,
    string PriceStatus,
    IReadOnlyList<string> Errors,
    string? StopReason,
    long? TokenCap,
    decimal? CostCap,
    RunAttemptEstimateDeviation? EstimateDeviation,
    IReadOnlyList<string> UsageLedgerMonths,
    RunAttemptQualitySummary? QualitySummary)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
}

public sealed record StoredRunArchive(
    RunArchiveRecord Record,
    IReadOnlyList<RunAttemptRecord> Attempts,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings);

/// <summary>
/// Persists the tracked, Git-committed run-history archive: one immutable manifest, append-only operation
/// and finding observations, and one create-only record per stopped attempt. Distinct from
/// <see cref="ReviewRunStore"/>, which remains the ignored, mutable crash-recovery journal.
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

    public void CreateRun(RunArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
    }

    public void AppendOperation(RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = RequireRunDirectory(operation.RunId);
        AppendLine(Path.Combine(directory, "operations.jsonl"), JsonSerializer.Serialize(operation, LineJsonOptions));
    }

    public void AppendFinding(RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = RequireRunDirectory(finding.RunId);
        AppendLine(Path.Combine(directory, "findings.jsonl"), JsonSerializer.Serialize(finding, LineJsonOptions));
    }

    public void CreateAttempt(RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentException("An attempt number must be 1 or greater.", nameof(attempt));
        var directory = Path.Combine(RequireRunDirectory(attempt.RunId), "attempts");
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, $"{attempt.Attempt:0000}.json"),
            JsonSerializer.Serialize(attempt, JsonOptions) + Environment.NewLine);
    }

    public StoredRunArchive? LoadRun(string runId)
    {
        var directory = FindRunDirectory(runId);
        if (directory is null) return null;
        var record = ReadRequired<RunArchiveRecord>(Path.Combine(directory, "run.json"));
        if (!string.Equals(record.RunId, runId, StringComparison.Ordinal))
            throw new InvalidDataException($"Run archive record disagrees about the run id in '{directory}'.");
        return new StoredRunArchive(record, ReadAttempts(directory, runId),
            ReadJsonLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl"))
                .Where(operation => string.Equals(operation.RunId, runId, StringComparison.Ordinal)).ToArray(),
            ReadJsonLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl"))
                .Where(finding => string.Equals(finding.RunId, runId, StringComparison.Ordinal)).ToArray());
    }

    private static IReadOnlyList<RunAttemptRecord> ReadAttempts(string directory, string runId)
    {
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        var attempts = new List<RunAttemptRecord>();
        foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var attempt = ReadRequired<RunAttemptRecord>(file);
            if (!string.Equals(attempt.RunId, runId, StringComparison.Ordinal))
                throw new InvalidDataException($"Run attempt file disagrees about the run id: '{file}'.");
            attempts.Add(attempt);
        }
        return attempts.OrderBy(attempt => attempt.Attempt).ToArray();
    }

    private static IReadOnlyList<T> ReadJsonLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
                if (item is not null) items.Add(item);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return items;
    }

    private string RequireRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new InvalidOperationException(
            $"Run archive '{runId}' does not exist. Create the run record before appending to it.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).OrderDescending(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (File.Exists(Path.Combine(candidate, "run.json"))) return candidate;
        }
        return null;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return Path.Combine(archiveRoot, month, runId);
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Run archive file is empty: {path}");

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendLine(string path, string json)
    {
        var line = Utf8.GetBytes(json + "\n");
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
}
