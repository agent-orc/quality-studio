using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveSourceRevision(string? Commit, bool? Dirty);

public sealed record RunArchiveRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    ReviewRunPlanNode Node,
    string Level,
    string Kind,
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    long? TokenCap = null,
    decimal? CostCap = null,
    ReviewRunEstimate? Estimate = null,
    RunArchiveSourceRevision? SourceRevision = null);

public sealed record RunArchiveGrade(int Score, string Band);

public sealed record RunOperationRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string OperationId,
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
    string? SubjectHash = null,
    string? ReviewedHash = null,
    string? SidecarPath = null,
    string? VerdictType = null,
    RunArchiveGrade? Grade = null,
    string? SecurityVerdict = null);

public sealed record RunFindingRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string OperationId,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<QualityFindingLocation> Locations,
    string State,
    DateTimeOffset ObservedAt);

public sealed record RunAttemptCounters(int Reviewed, int Failed, int Skipped, int Cancelled);

public sealed record RunAttemptQualitySummary(
    int? LowestGradeScore,
    string? LowestGradeBand,
    string? WorstSecurityVerdict,
    int ActiveFindings,
    string? HighestActiveSeverity);

public sealed record RunAttemptRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    int Attempt,
    string Outcome,
    string Completeness,
    RunAttemptCounters Counters,
    RunAttemptCounters CumulativeCounters,
    TokenUsage Spend,
    string PriceStatus,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<string> LedgerReferences,
    DateTimeOffset ArchivedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    decimal? Cost = null,
    string? Currency = null,
    long? TokenCap = null,
    decimal? CostCap = null,
    decimal? EstimateDeviationPercent = null,
    RunAttemptQualitySummary? QualitySummary = null);

public sealed record StoredRunArchive(
    RunArchiveRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Serializes the four run-history v1 schemas with a stable $schema/schemaVersion-first field order
/// and rejects documents that do not carry their own schema identity.
/// </summary>
public static class ReviewRunArchiveJson
{
    public const string RunRecordSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string RunOperationSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string RunFindingSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string RunAttemptSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    public static JsonSerializerOptions DocumentOptions { get; } = CreateOptions(indented: true);
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(indented: false);

    public static RunArchiveRecord WithSchema(this RunArchiveRecord record) =>
        record with { Schema = RunRecordSchemaId, SchemaVersion = 1 };

    public static RunOperationRecord WithSchema(this RunOperationRecord record) =>
        record with { Schema = RunOperationSchemaId, SchemaVersion = 1 };

    public static RunFindingRecord WithSchema(this RunFindingRecord record) =>
        record with { Schema = RunFindingSchemaId, SchemaVersion = 1 };

    public static RunAttemptRecord WithSchema(this RunAttemptRecord record) =>
        record with { Schema = RunAttemptSchemaId, SchemaVersion = 1 };

    public static void Validate(RunArchiveRecord record)
    {
        if (record.SchemaVersion != 1 || !string.Equals(record.Schema, RunRecordSchemaId, StringComparison.Ordinal))
            throw new JsonException("Unsupported run archive record schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RepositoryId);
    }

    public static void Validate(RunOperationRecord record)
    {
        if (record.SchemaVersion != 1 || !string.Equals(record.Schema, RunOperationSchemaId, StringComparison.Ordinal))
            throw new JsonException("Unsupported run archive operation schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(record.OperationId);
        if (record.Attempt < 1) throw new JsonException("A run operation attempt must be positive.");
    }

    public static void Validate(RunFindingRecord record)
    {
        if (record.SchemaVersion != 1 || !string.Equals(record.Schema, RunFindingSchemaId, StringComparison.Ordinal))
            throw new JsonException("Unsupported run archive finding schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(record.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Fingerprint);
    }

    public static void Validate(RunAttemptRecord record)
    {
        if (record.SchemaVersion != 1 || !string.Equals(record.Schema, RunAttemptSchemaId, StringComparison.Ordinal))
            throw new JsonException("Unsupported run archive attempt schema.");
        if (record.Attempt < 1) throw new JsonException("A run archive attempt number must be positive.");
        if (record.Outcome is not ("done" or "failed" or "cancelled" or "capped"))
            throw new JsonException("A run archive attempt outcome must be done, failed, cancelled, or capped.");
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>
/// Tracked, repository-owned archive of stopped review-run attempts. Unlike <see cref="ReviewRunStore"/>,
/// every file here is create-only or append-only: nothing is ever rewritten in place, and nothing here
/// is Git-ignored. The mutable recovery journal under .quality/runs remains the sole owner of in-flight state.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
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
        var stamped = record.WithSchema();
        ReviewRunArchiveJson.Validate(stamped);
        var directory = RunDirectory(stamped.RunId, stamped.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.DocumentOptions) + Environment.NewLine);
    }

    public void AppendOperation(RunOperationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var stamped = record.WithSchema();
        ReviewRunArchiveJson.Validate(stamped);
        var directory = RequireRunDirectory(stamped.RunId);
        AppendLine(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.LineOptions));
    }

    public void AppendFinding(string runId, RunFindingRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(record);
        var stamped = record.WithSchema();
        ReviewRunArchiveJson.Validate(stamped);
        var directory = RequireRunDirectory(runId);
        AppendLine(Path.Combine(directory, "findings.jsonl"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.LineOptions));
    }

    public void WriteAttempt(RunAttemptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var stamped = record.WithSchema();
        ReviewRunArchiveJson.Validate(stamped);
        var directory = RequireRunDirectory(stamped.RunId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        WriteCreateOnly(Path.Combine(attemptsDirectory, $"{stamped.Attempt:D4}.json"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.DocumentOptions) + Environment.NewLine);
    }

    public StoredRunArchive? TryLoadRun(string runId)
    {
        var directory = FindRunDirectory(runId);
        if (directory is null) return null;

        var run = JsonSerializer.Deserialize<RunArchiveRecord>(
            File.ReadAllText(Path.Combine(directory, "run.json")), ReviewRunArchiveJson.DocumentOptions)
            ?? throw new InvalidDataException($"Run archive record is empty: {directory}");
        ReviewRunArchiveJson.Validate(run);

        var operations = ReadLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl"));
        var findings = ReadLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl"));

        var attemptsDirectory = Path.Combine(directory, "attempts");
        var attempts = new List<RunAttemptRecord>();
        if (Directory.Exists(attemptsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                var attempt = JsonSerializer.Deserialize<RunAttemptRecord>(File.ReadAllText(path), ReviewRunArchiveJson.DocumentOptions)
                    ?? throw new InvalidDataException($"Run archive attempt is empty: {path}");
                ReviewRunArchiveJson.Validate(attempt);
                attempts.Add(attempt);
            }
        }

        return new StoredRunArchive(run, operations, findings, attempts.OrderBy(attempt => attempt.Attempt).ToArray());
    }

    private string RequireRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new DirectoryNotFoundException(
            $"Review run '{runId}' has no archived run record. Call {nameof(CreateRun)} first.");

    /// <summary>Scans the bounded set of month folders for the archived run directory matching this id.</summary>
    private string? FindRunDirectory(string runId)
    {
        var safeRunId = SafeRunId(runId);
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, safeRunId);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "run.json")))
                return candidate;
        }
        return null;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        var safeRunId = SafeRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(archiveRoot, month, safeRunId);
    }

    private static string SafeRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            runId is "." or "..")
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
        return runId;
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
                var item = JsonSerializer.Deserialize<T>(line, ReviewRunArchiveJson.LineOptions);
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

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendLine(string path, string content)
    {
        var line = Utf8.GetBytes(content + "\n");
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
