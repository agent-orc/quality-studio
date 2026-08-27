using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveNode(string Id, string Name, string Path);

public sealed record RunArchiveTarget(string Id, string Name, string Path, string SubjectHash);

/// <summary>
/// Immutable identity, plan and configuration for one review run, archived once at
/// <c>.quality/run-history/&lt;YYYY-MM&gt;/&lt;runId&gt;/run.json</c>. Create-only; never rewritten.
/// </summary>
public sealed record RunRecord(
    string RunId,
    string RepositoryId,
    RunArchiveNode Node,
    string Level,
    string Kind,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RunArchiveTarget> Targets,
    bool Force,
    long? TokenCap = null,
    decimal? CostCap = null,
    string? SourceCommit = null,
    bool? SourceDirty = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
/// Append-only line in <c>operations.jsonl</c> connecting plan, output, token usage and findings
/// for a single review operation without relying on the latest sidecar.
/// </summary>
public sealed record RunOperationRecord(
    string RunId,
    string OperationId,
    int Ordinal,
    int Attempt,
    string Path,
    string Level,
    string State,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? ProviderRunId = null,
    string? SubjectHash = null,
    string? ReviewInputHash = null,
    string? SidecarPath = null,
    string? SidecarSha256 = null,
    int? GradeScore = null,
    string? GradeBand = null,
    string? SecurityVerdict = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

public sealed record RunFindingLocation(string Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

/// <summary>
/// Append-only line in <c>findings.jsonl</c> recording what one operation observed, including
/// lifecycle state at that time. Never becomes the current lifecycle owner.
/// </summary>
public sealed record RunFindingRecord(
    string RunId,
    string OperationId,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<RunFindingLocation> Locations,
    string State,
    DateTimeOffset ObservedAt)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
/// Create-only snapshot of one stopped attempt (done/failed/cancelled/capped) under a logical run.
/// A capped run that resumes later adds another attempt and never rewrites an earlier one.
/// </summary>
public sealed record RunAttemptRecord(
    string RunId,
    int Attempt,
    string Outcome,
    string Completeness,
    DateTimeOffset ArchivedAt,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    TokenUsage Usage,
    string PriceStatus,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> UsageLedgerMonths,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    decimal? CostSpent = null,
    string? Currency = null,
    long? TokenCap = null,
    decimal? CostCap = null,
    string? StopReason = null)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(-2)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(-1)]
    public int SchemaVersion { get; init; } = 1;
}

public sealed record StoredRunArchive(
    RunRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Persists the tracked, append-only run-history archive described in the run-persistence dossier
/// (RP-1). This is a separate contract from <see cref="ReviewRunStore"/>, which keeps owning the
/// ignored, mutable crash-recovery journal. The archive is never auto-deleted or rewritten in place.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string archivePath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archivePath = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchivePath => archivePath;

    public void CreateRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
    }

    public void AppendOperation(DateTimeOffset runCreatedAt, RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        AppendLine(runCreatedAt, operation.RunId, "operations.jsonl", JsonSerializer.Serialize(operation, LineJsonOptions));
    }

    public void AppendFinding(DateTimeOffset runCreatedAt, RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        AppendLine(runCreatedAt, finding.RunId, "findings.jsonl", JsonSerializer.Serialize(finding, LineJsonOptions));
    }

    /// <summary>Returns the next create-only attempt number for a run, starting at 1.</summary>
    public int NextAttemptOrdinal(string runId, DateTimeOffset runCreatedAt)
    {
        var directory = Path.Combine(RunDirectory(runId, runCreatedAt), "attempts");
        if (!Directory.Exists(directory)) return 1;
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var ordinal) && ordinal > highest)
                highest = ordinal;
        }
        return highest + 1;
    }

    public void WriteAttempt(DateTimeOffset runCreatedAt, RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentException("An attempt number must be 1 or greater.", nameof(attempt));
        var directory = Path.Combine(RunDirectory(attempt.RunId, runCreatedAt), "attempts");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{attempt.Attempt:0000}.json");
        WriteCreateOnly(destination, JsonSerializer.Serialize(attempt, JsonOptions) + Environment.NewLine);
    }

    /// <summary>Loads an archived run when its creation month is already known.</summary>
    public StoredRunArchive? TryLoad(string runId, DateTimeOffset runCreatedAt)
    {
        var directory = RunDirectory(runId, runCreatedAt);
        return Directory.Exists(directory) ? Load(directory) : null;
    }

    /// <summary>Loads an archived run by scanning month folders when the creation month is unknown.</summary>
    public StoredRunArchive? TryFind(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archivePath)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archivePath).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (Directory.Exists(candidate)) return Load(candidate);
        }
        return null;
    }

    private StoredRunArchive Load(string directory)
    {
        var run = ReadRequired<RunRecord>(Path.Combine(directory, "run.json"));
        var operations = ReadLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl"));
        var findings = ReadLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl"));
        var attemptsDirectory = Path.Combine(directory, "attempts");
        var attempts = Directory.Exists(attemptsDirectory)
            ? Directory.EnumerateFiles(attemptsDirectory, "*.json")
                .Order(StringComparer.Ordinal)
                .Select(ReadRequired<RunAttemptRecord>)
                .OrderBy(attempt => attempt.Attempt)
                .ToArray()
            : [];
        return new StoredRunArchive(run, operations, findings, attempts);
    }

    private void AppendLine(DateTimeOffset runCreatedAt, string runId, string fileName, string json)
    {
        var directory = RunDirectory(runId, runCreatedAt);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
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

    private string RunDirectory(string runId, DateTimeOffset runCreatedAt)
    {
        ValidateRunId(runId);
        var month = runCreatedAt.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return Path.Combine(archivePath, month, runId);
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
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
