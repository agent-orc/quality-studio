using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveRecord(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    ReviewRunPlanNode Node,
    string Level,
    string Kind,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunEstimate? Estimate,
    string? SourceCommit = null,
    bool? SourceDirty = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record RunOperationVerdict(
    string Kind,
    int? GradeScore = null,
    string? GradeBand = null,
    string? SecurityVerdict = null);

public sealed record RunOperationRecord(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string OperationId,
    string RunId,
    int Attempt,
    int Ordinal,
    string UnitId,
    string Path,
    string Level,
    string State,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? ProviderRunId = null,
    string? ReviewedHash = null,
    string? InputHash = null,
    string? SidecarPath = null,
    RunOperationVerdict? Verdict = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record RunFindingLocation(
    string Path,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);

public sealed record RunFindingRecord(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string OperationId,
    string RunId,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    string State,
    IReadOnlyList<RunFindingLocation> Locations,
    DateTimeOffset ObservedAt)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record RunAttemptCap(
    long? TokenLimit,
    decimal? CostLimit,
    string Outcome);

public sealed record RunAttemptQualitySummary(
    int? LowestGradeScore,
    string? LowestGradeBand,
    string? WorstSecurityVerdict,
    int ActiveFindingCount,
    string? HighestActiveSeverity);

public sealed record RunAttemptRecord(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    int AttemptNumber,
    string Outcome,
    string Completeness,
    DateTimeOffset ArchivedAt,
    int Reviewed,
    int Failed,
    int Skipped,
    int Cancelled,
    TokenUsage Usage,
    string PriceStatus,
    IReadOnlyList<string> ErrorCodes,
    RunAttemptCap Cap,
    IReadOnlyList<string> UsageLedgerMonths,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    decimal? CostSpent = null,
    string? Currency = null,
    string? StopReason = null,
    RunAttemptQualitySummary? QualitySummary = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
}

public sealed record StoredRunArchive(
    RunArchiveRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Persists the tracked, append-only run-history archive described by the run-persistence
/// dossier. This store is additive: it never touches <see cref="ReviewRunStore"/>'s ignored
/// recovery journal and nothing yet calls it from the live review pipeline.
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

    /// <summary>Creates the immutable run record. Throws if the run already exists.</summary>
    public void CreateRun(RunArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.CreatedAt, record.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), Serialize(record));
    }

    public void AppendOperation(RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = FindRunDirectory(operation.RunId);
        AppendLine(Path.Combine(directory, "operations.jsonl"), operation);
    }

    public void AppendFinding(RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = FindRunDirectory(finding.RunId);
        AppendLine(Path.Combine(directory, "findings.jsonl"), finding);
    }

    /// <summary>Creates the next attempt record. Throws if that attempt number already exists.</summary>
    public void WriteAttempt(RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.AttemptNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt), "An attempt number must be 1 or greater.");
        var directory = FindRunDirectory(attempt.RunId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var fileName = attempt.AttemptNumber.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        WriteCreateOnly(Path.Combine(attemptsDirectory, fileName), Serialize(attempt));
    }

    /// <summary>Reads back a full archived run for verification and future history/diff readers.</summary>
    public StoredRunArchive Load(string runId)
    {
        var directory = FindRunDirectory(runId);
        var run = ReadRequired<RunArchiveRecord>(Path.Combine(directory, "run.json"));
        return new StoredRunArchive(run, ReadLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl")),
            ReadLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl")), ReadAttempts(directory));
    }

    private IReadOnlyList<RunAttemptRecord> ReadAttempts(string directory)
    {
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        return Directory.EnumerateFiles(attemptsDirectory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(ReadRequired<RunAttemptRecord>)
            .ToArray();
    }

    private string FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (Directory.Exists(archiveRoot))
        {
            foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(monthDirectory, runId);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException($"No archived run history was found for run id '{runId}'.");
    }

    private string RunDirectory(DateTimeOffset createdAt, string runId)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(archiveRoot, month, runId);
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;

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

    private static IReadOnlyList<T> ReadLines<T>(string path)
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

    private static T ReadRequired<T>(string path) =>
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
}
