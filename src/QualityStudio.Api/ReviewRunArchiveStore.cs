using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveTarget(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Name,
    [property: JsonPropertyOrder(2)] string Path,
    [property: JsonPropertyOrder(3)] string SubjectHash);

public sealed record RunArchiveSubject(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] string Path,
    [property: JsonPropertyOrder(2)] string Level);

public sealed record RunArchiveConfiguration(
    [property: JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model,
    [property: JsonPropertyOrder(1)] string CliType,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel,
    [property: JsonPropertyOrder(3)] bool Force,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokenCap = null,
    [property: JsonPropertyOrder(5), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostCap = null);

public sealed record RunArchiveSourceRevision(
    [property: JsonPropertyOrder(0)] string Commit,
    [property: JsonPropertyOrder(1)] bool Dirty);

/// <summary>Immutable identity, plan and configuration for one review run. Create-only.</summary>
public sealed record RunRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)] public required string RunId { get; init; }
    [JsonPropertyOrder(3)] public required string RepositoryId { get; init; }
    [JsonPropertyOrder(4)] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyOrder(5)] public required RunArchiveSubject Subject { get; init; }
    [JsonPropertyOrder(6)] public required string Kind { get; init; }
    [JsonPropertyOrder(7)] public required IReadOnlyList<RunArchiveTarget> Targets { get; init; }
    [JsonPropertyOrder(8)] public required RunArchiveConfiguration Configuration { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunArchiveSourceRevision? SourceRevision { get; init; }
}

/// <summary>Typed quality verdict for one operation. Operational outcome and quality verdict are kept separate on purpose.</summary>
public sealed record RunOperationVerdict
{
    [JsonPropertyOrder(0)] public required string Kind { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradeSnapshot? Grade { get; init; }

    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityVerdict? Security { get; init; }

    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>One append-only observation of a completed or failed review operation.</summary>
public sealed record RunOperationRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)] public required string RunId { get; init; }
    [JsonPropertyOrder(3)] public required string OperationId { get; init; }
    [JsonPropertyOrder(4)] public required int Ordinal { get; init; }
    [JsonPropertyOrder(5)] public required int Attempt { get; init; }
    [JsonPropertyOrder(6)] public required string UnitId { get; init; }
    [JsonPropertyOrder(7)] public required string Path { get; init; }
    [JsonPropertyOrder(8)] public required string Level { get; init; }
    [JsonPropertyOrder(9)] public required string State { get; init; }
    [JsonPropertyOrder(10)] public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderRunId { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewedHash { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SidecarPath { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunOperationVerdict? Verdict { get; init; }
}

/// <summary>One append-only finding observation captured at review time. Never becomes the current lifecycle owner.</summary>
public sealed record RunFindingRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)] public required string RunId { get; init; }
    [JsonPropertyOrder(3)] public required string OperationId { get; init; }
    [JsonPropertyOrder(4)] public required string Fingerprint { get; init; }
    [JsonPropertyOrder(5)] public required string FindingId { get; init; }
    [JsonPropertyOrder(6)] public required string RuleId { get; init; }
    [JsonPropertyOrder(7)] public required FindingSeverity Severity { get; init; }
    [JsonPropertyOrder(8)] public required string Title { get; init; }
    [JsonPropertyOrder(9)] public required IReadOnlyList<FindingLocation> Locations { get; init; }
    [JsonPropertyOrder(10)] public required string State { get; init; }
    [JsonPropertyOrder(11)] public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record RunAttemptCounters(
    [property: JsonPropertyOrder(0)] int TotalFiles,
    [property: JsonPropertyOrder(1)] int CompletedFiles,
    [property: JsonPropertyOrder(2)] int FailedFiles,
    [property: JsonPropertyOrder(3)] int SkippedFiles);

public sealed record RunAttemptSpend(
    [property: JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? InputTokens,
    [property: JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OutputTokens,
    [property: JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Cost,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency);

public sealed record RunAttemptQualitySummary
{
    [JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GradeSnapshot? LowestGrade { get; init; }

    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityVerdict? WorstSecurityVerdict { get; init; }

    [JsonPropertyOrder(2)] public required int ActiveFindingCount { get; init; }

    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FindingSeverity? HighestActiveSeverity { get; init; }
}

/// <summary>Create-only snapshot of one stopped attempt. A resumed capped run adds another attempt; an earlier one is never rewritten.</summary>
public sealed record RunAttemptRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)] public required string RunId { get; init; }
    [JsonPropertyOrder(3)] public required int Attempt { get; init; }
    [JsonPropertyOrder(4)] public required string Outcome { get; init; }
    [JsonPropertyOrder(5)] public required RunAttemptCounters Counters { get; init; }
    [JsonPropertyOrder(6)] public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunAttemptSpend? Spend { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ErrorCodes { get; init; }

    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LedgerReferences { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunAttemptQualitySummary? QualitySummary { get; init; }
}

public sealed record StoredRunArchive(
    RunRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Persists the tracked, Git-visible run-history archive under .quality/run-history/YYYY-MM/&lt;runId&gt;/.
/// This is a separate store from <see cref="ReviewRunStore"/>, which keeps owning the ignored,
/// mutable .quality/runs/ crash-recovery journal. Nothing here mutates that journal or its routes.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";

    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions DocumentOptions = ReviewMetaJson.Options;
    private static readonly JsonSerializerOptions LineOptions = new(ReviewMetaJson.Options) { WriteIndented = false };

    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = System.IO.Path.Combine(System.IO.Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    public void CreateRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectoryForCreate(record.RunId, record.CreatedAt);
        PathConfinement.RejectReparseTraversal(archiveRoot, directory);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(System.IO.Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(record, DocumentOptions) + Environment.NewLine);
    }

    public void AppendOperation(RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = RequireRunDirectory(operation.RunId);
        AppendLine(System.IO.Path.Combine(directory, "operations.jsonl"), operation);
    }

    public void AppendFinding(RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = RequireRunDirectory(finding.RunId);
        AppendLine(System.IO.Path.Combine(directory, "findings.jsonl"), finding);
    }

    public void WriteAttempt(RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentException("A run attempt number must be at least 1.", nameof(attempt));
        var directory = RequireRunDirectory(attempt.RunId);
        var attemptsDirectory = System.IO.Path.Combine(directory, "attempts");
        PathConfinement.RejectReparseTraversal(archiveRoot, attemptsDirectory);
        Directory.CreateDirectory(attemptsDirectory);
        var destination = System.IO.Path.Combine(attemptsDirectory,
            attempt.Attempt.ToString("0000", CultureInfo.InvariantCulture) + ".json");
        WriteCreateOnly(destination, JsonSerializer.Serialize(attempt, DocumentOptions) + Environment.NewLine);
    }

    public StoredRunArchive? LoadRun(string runId)
    {
        var directory = FindRunDirectory(runId);
        if (directory is null) return null;

        var runPath = System.IO.Path.Combine(directory, "run.json");
        if (!File.Exists(runPath)) return null;
        var run = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(runPath), DocumentOptions)
                  ?? throw new InvalidDataException($"Run archive record is empty: {runPath}");

        return new StoredRunArchive(
            run,
            ReadLines<RunOperationRecord>(System.IO.Path.Combine(directory, "operations.jsonl")),
            ReadLines<RunFindingRecord>(System.IO.Path.Combine(directory, "findings.jsonl")),
            ReadAttempts(directory));
    }

    private IReadOnlyList<RunAttemptRecord> ReadAttempts(string directory)
    {
        var attemptsDirectory = System.IO.Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        var attempts = new List<RunAttemptRecord>();
        foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var attempt = JsonSerializer.Deserialize<RunAttemptRecord>(File.ReadAllText(file), DocumentOptions)
                          ?? throw new InvalidDataException($"Run archive attempt record is empty: {file}");
            attempts.Add(attempt);
        }
        return attempts;
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
                var record = JsonSerializer.Deserialize<T>(line, LineOptions);
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

    private void AppendLine<T>(string path, T record)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(JsonSerializer.Serialize(record, LineOptions) + "\n");
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

    private string RequireRunDirectory(string runId)
    {
        return FindRunDirectory(runId)
               ?? throw new InvalidOperationException(
                   $"Review run archive '{runId}' does not exist. Call {nameof(CreateRun)} first.");
    }

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
        {
            var candidate = System.IO.Path.Combine(monthDirectory, runId);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string RunDirectoryForCreate(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(archiveRoot, month, runId);
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, System.IO.Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
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
