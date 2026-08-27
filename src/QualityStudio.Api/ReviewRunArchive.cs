using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewRunArchiveTarget(string UnitId, string Name, string Path, string SubjectHash);

public sealed record ReviewRunArchiveEstimate(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunArchiveSourceRevision(string? CommitSha, bool? Dirty);

public sealed record ReviewRunArchiveGrade(int Score, string Band);

public sealed record ReviewRunArchiveVerdict(string Kind, ReviewRunArchiveGrade? Grade, string? SecurityVerdict);

public sealed record ReviewRunArchiveLocation(string Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

public sealed record ReviewRunArchiveCounters(int TotalFiles, int CompletedFiles, int FailedFiles, int SkippedFiles);

public sealed record ReviewRunArchiveCap(long? TokenLimit, decimal? CostLimit, string Outcome, string? Reason);

public sealed record ReviewRunArchiveEstimateDeviation(
    decimal? InputTokensPercent, decimal? OutputTokensPercent, decimal? CostPercent);

public sealed record ReviewRunArchiveQualitySummary(
    ReviewRunArchiveGrade? LowestGrade,
    string? WorstSecurityVerdict,
    int ActiveFindingCount,
    string? HighestActiveSeverity);

/// <summary>
/// Immutable identity, plan and configuration for one archived review run under
/// <c>.quality/run-history/&lt;yyyy-MM&gt;/&lt;runId&gt;/run.json</c>. Create-only: it is written once by
/// <see cref="ReviewRunArchiveStore.CreateRun"/> and never rewritten. Attempts, operations and finding
/// observations grow beside it afterward.
/// </summary>
public sealed record ReviewRunArchiveRecord(
    string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    string UnitId,
    string Level,
    string Path,
    string Kind,
    string? Model,
    string CliType,
    string? ThinkingLevel,
    bool Force,
    IReadOnlyList<ReviewRunArchiveTarget> Targets,
    string TargetsManifestHash,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunArchiveEstimate? Estimate,
    ReviewRunArchiveSourceRevision? SourceRevision)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

/// <summary>
/// One append-only line in <c>operations.jsonl</c>, written after a review operation's sidecar result is
/// persisted or its terminal error is known. Connects the archived plan, the produced sidecar and the
/// operation's typed verdict without relying on the latest sidecar.
/// </summary>
public sealed record ReviewRunArchiveOperationRecord(
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
    string? SidecarPath,
    string? SidecarSha256,
    ReviewRunArchiveVerdict Verdict)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

/// <summary>
/// One append-only line in <c>findings.jsonl</c>: what one operation observed, including the finding's
/// lifecycle state at that time. Never becomes the current lifecycle owner; <c>.quality/findings/state.json</c>
/// keeps that role.
/// </summary>
public sealed record ReviewRunArchiveFindingRecord(
    string OperationId,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<ReviewRunArchiveLocation> Locations,
    string State)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

/// <summary>
/// One create-only <c>attempts/NNNN.json</c> snapshot per stopped attempt of a logical run. A capped run
/// that later resumes adds another attempt; it never rewrites an earlier one. <c>capped</c> is a stopped
/// attempt, not an immutable end of the logical run.
/// </summary>
public sealed record ReviewRunArchiveAttemptRecord(
    int Attempt,
    string Outcome,
    string Completeness,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    ReviewRunArchiveCounters Counters,
    ReviewRunArchiveCounters CumulativeCounters,
    TokenUsage Usage,
    decimal? Cost,
    string? Currency,
    string PriceStatus,
    IReadOnlyList<string> ErrorCodes,
    ReviewRunArchiveCap Cap,
    ReviewRunArchiveEstimateDeviation? EstimateDeviation,
    IReadOnlyList<string> UsageLedgerMonths,
    ReviewRunArchiveQualitySummary QualitySummary,
    DateTimeOffset ArchivedAt)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}

/// <summary>Every archived record for one run, as read back from <see cref="ReviewRunArchiveStore"/>.</summary>
public sealed record StoredReviewRunArchive(
    ReviewRunArchiveRecord Run,
    IReadOnlyList<ReviewRunArchiveAttemptRecord> Attempts,
    IReadOnlyList<ReviewRunArchiveOperationRecord> Operations,
    IReadOnlyList<ReviewRunArchiveFindingRecord> Findings)
{
    /// <summary>The highest valid attempt number, i.e. the latest known state of the logical run.</summary>
    public ReviewRunArchiveAttemptRecord? LatestAttempt => Attempts.Count == 0 ? null : Attempts[^1];
}

public static class ReviewRunArchiveJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = indented,
    };
}

/// <summary>
/// Persists the tracked, Git-committed run history archive under <c>.quality/run-history/</c>. This is a
/// separate store from the ignored <c>.quality/runs/</c> recovery journal owned by <see cref="ReviewRunStore"/>:
/// the journal is a mutable cache, this store is append/create-only historical truth.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    public void CreateRun(ReviewRunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var directory = MonthDirectory(run.RunId, run.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(run, ReviewRunArchiveJson.Options) + Environment.NewLine);
    }

    public void AppendOperation(string runId, ReviewRunArchiveOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        AppendLine(Path.Combine(ExistingRunDirectory(runId), "operations.jsonl"),
            JsonSerializer.Serialize(operation, ReviewRunArchiveJson.LineOptions));
    }

    public void AppendFinding(string runId, ReviewRunArchiveFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        AppendLine(Path.Combine(ExistingRunDirectory(runId), "findings.jsonl"),
            JsonSerializer.Serialize(finding, ReviewRunArchiveJson.LineOptions));
    }

    public void WriteAttempt(string runId, ReviewRunArchiveAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), "An archived attempt number must be 1 or greater.");
        var attemptsDirectory = Path.Combine(ExistingRunDirectory(runId), "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        WriteCreateOnly(Path.Combine(attemptsDirectory, $"{attempt.Attempt:D4}.json"),
            JsonSerializer.Serialize(attempt, ReviewRunArchiveJson.Options) + Environment.NewLine);
    }

    public StoredReviewRunArchive LoadRun(string runId)
    {
        var directory = ExistingRunDirectory(runId);
        var run = ReadRequired<ReviewRunArchiveRecord>(Path.Combine(directory, "run.json"));
        if (!string.Equals(run.RunId, runId, StringComparison.Ordinal))
            throw new InvalidDataException($"Archived run record disagrees about the run id in '{directory}'.");
        return new StoredReviewRunArchive(
            run,
            ReadAttempts(directory),
            ReadJsonl<ReviewRunArchiveOperationRecord>(Path.Combine(directory, "operations.jsonl")),
            ReadJsonl<ReviewRunArchiveFindingRecord>(Path.Combine(directory, "findings.jsonl")));
    }

    public bool TryFindRunDirectory(string runId, out string? directory)
    {
        directory = FindRunDirectory(runId);
        return directory is not null;
    }

    private string MonthDirectory(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var directory = Path.Combine(archiveRoot, month, runId);
        if (!PathConfinement.IsWithin(archiveRoot, directory))
            throw new ArgumentException("Archived run path escapes the configured archive root.", nameof(runId));
        return directory;
    }

    private string ExistingRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new DirectoryNotFoundException(
            $"No archived run history was found for run '{runId}'.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (!Directory.Exists(candidate) || !PathConfinement.IsWithin(archiveRoot, candidate)) continue;
            PathConfinement.RejectReparseTraversal(archiveRoot, candidate);
            return candidate;
        }
        return null;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId is "." or ".." ||
            !string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static IReadOnlyList<ReviewRunArchiveAttemptRecord> ReadAttempts(string directory)
    {
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        var attempts = Directory.EnumerateFiles(attemptsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(ReadRequired<ReviewRunArchiveAttemptRecord>)
            .OrderBy(attempt => attempt.Attempt)
            .ToArray();
        return attempts;
    }

    private static IReadOnlyList<T> ReadJsonl<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<T>(line, ReviewRunArchiveJson.LineOptions);
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

    private static T ReadRequired<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReviewRunArchiveJson.Options)
        ?? throw new InvalidDataException($"Archived run file is empty: {path}");

    private static void AppendLine(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
