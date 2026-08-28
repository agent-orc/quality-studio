using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Immutable identity, plan and configuration for one archived review run (contract run-record.v1).</summary>
public sealed record RunRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-record.v1.schema.json";

    [property: JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string RunId { get; init; }
    public required string RepositoryId { get; init; }
    public required ReviewRunPlanNode Node { get; init; }
    public required string Level { get; init; }
    public required string Kind { get; init; }
    public string? Model { get; init; }
    public required string CliType { get; init; }
    public bool Force { get; init; }
    public string? ThinkingLevel { get; init; }
    public long? TokenCap { get; init; }
    public decimal? CostCap { get; init; }
    public required IReadOnlyList<ReviewRunPlanTarget> Targets { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? SourceRevision { get; init; }
    public bool? SourceDirty { get; init; }
}

/// <summary>One completed, failed, cancelled or skipped operation observed during a run (contract run-operation.v1).</summary>
public sealed record RunOperationRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-operation.v1.schema.json";

    [property: JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string RunId { get; init; }
    public required string OperationId { get; init; }
    public required int Attempt { get; init; }
    public required int Ordinal { get; init; }
    public required string Path { get; init; }
    public required string Level { get; init; }
    public required string State { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? ProviderRunId { get; init; }
    public string? SubjectHash { get; init; }
    public string? InputHash { get; init; }
    public string? ResultSidecarPath { get; init; }
    public GradeSnapshot? Grade { get; init; }
    public string? VerdictKind { get; init; }
    public string? Verdict { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>One finding observation captured by an operation, independent of the current lifecycle projection
/// (contract run-finding.v1).</summary>
public sealed record RunFindingRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-finding.v1.schema.json";

    [property: JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string RunId { get; init; }
    public required string OperationId { get; init; }
    public required string Fingerprint { get; init; }
    public required string FindingId { get; init; }
    public required string RuleId { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<FindingLocation> Locations { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Deterministic rollups for an attempt. Never a repository-wide pass/fail threshold.</summary>
public sealed record RunAttemptQualitySummary
{
    public GradeSnapshot? LowestGrade { get; init; }
    public string? WorstSecurityVerdict { get; init; }
    public int? ActiveFindingCount { get; init; }
    public string? HighestActiveSeverity { get; init; }
}

/// <summary>A single, create-only stopped attempt of a run: capped, failed, cancelled or done
/// (contract run-attempt.v1). A resumed capped run adds another attempt; it never rewrites the earlier one.</summary>
public sealed record RunAttemptRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-attempt.v1.schema.json";

    [property: JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string RunId { get; init; }
    public required int Attempt { get; init; }
    public required string Outcome { get; init; }
    public required string Completeness { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public required DateTimeOffset ArchivedAt { get; init; }
    public required int TotalFiles { get; init; }
    public required int CompletedFiles { get; init; }
    public required int FailedFiles { get; init; }
    public required int SkippedFiles { get; init; }
    public required TokenUsage Usage { get; init; }
    public decimal? CostSpent { get; init; }
    public string? Currency { get; init; }
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public string? StopReason { get; init; }
    public IReadOnlyList<string> LedgerMonths { get; init; } = [];
    public RunAttemptQualitySummary? QualitySummary { get; init; }
}

/// <summary>Everything archived for one run: its identity, every observed operation and finding, and every
/// stopped attempt, ordered oldest first.</summary>
public sealed record StoredRunArchive(
    RunRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Persists the tracked, Git-visible run-history archive beside the existing ignored recovery journal
/// (<see cref="ReviewRunStore"/>). Every document is create-only or append-only; nothing here is ever rewritten.
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
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    /// <summary>Create-only. Throws if a run archive already exists for this run id.</summary>
    public string CreateRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = DirectoryForNewRun(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), Serialize(record));
        return directory;
    }

    /// <summary>Append-only. The run must already have an archived <c>run.json</c>.</summary>
    public void AppendOperation(RunOperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        AppendLine(Path.Combine(RequireRunDirectory(record.RunId), "operations.jsonl"), record);
    }

    /// <summary>Append-only. The run must already have an archived <c>run.json</c>.</summary>
    public void AppendFinding(RunFindingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        AppendLine(Path.Combine(RequireRunDirectory(record.RunId), "findings.jsonl"), record);
    }

    /// <summary>Create-only at its attempt ordinal. A later resume adds the next attempt and never
    /// rewrites an earlier one.</summary>
    public void WriteAttempt(RunAttemptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Attempt < 1)
            throw new ArgumentException("Attempt numbers start at 1.", nameof(record));
        var attemptsDirectory = Path.Combine(RequireRunDirectory(record.RunId), "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var path = Path.Combine(attemptsDirectory, $"{record.Attempt:0000}.json");
        WriteCreateOnly(path, Serialize(record));
    }

    /// <summary>Reads the full archive for one run. Attempts are ordered oldest first by their ordinal.</summary>
    public StoredRunArchive Load(string runId)
    {
        var directory = RequireRunDirectory(runId);
        var run = ReadRequired<RunRecord>(Path.Combine(directory, "run.json"));
        var operations = ReadLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl"));
        var findings = ReadLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl"));
        var attemptsDirectory = Path.Combine(directory, "attempts");
        var attempts = Directory.Exists(attemptsDirectory)
            ? Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal)
                .Select(ReadRequired<RunAttemptRecord>).ToArray()
            : [];
        return new StoredRunArchive(run, operations, findings, attempts);
    }

    private string DirectoryForNewRun(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var directory = Path.Combine(archiveRoot, month, runId);
        PathConfinement.RejectReparseTraversal(archiveRoot, directory);
        return directory;
    }

    /// <summary>Locates an already-archived run by scanning month folders; the archive does not require
    /// callers to remember which month a run was created in.</summary>
    private string RequireRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (Directory.Exists(archiveRoot))
        {
            foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(monthDirectory, runId);
                if (!File.Exists(Path.Combine(candidate, "run.json"))) continue;
                PathConfinement.RejectReparseTraversal(archiveRoot, candidate);
                return candidate;
            }
        }
        throw new DirectoryNotFoundException($"No archived run history exists for run '{runId}'.");
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendLine<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(JsonSerializer.Serialize(value, LineJsonOptions) + "\n");
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

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Review run archive file is empty: {path}");

    private static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
            if (item is not null) items.Add(item);
        }
        return items;
    }
}
