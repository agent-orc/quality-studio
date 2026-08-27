using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class ReviewRunArchiveSchemas
{
    public const int Version = 1;
    public const string Run = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string Operation = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string Finding = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string Attempt = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
}

public sealed record ReviewRunArchiveSubject(string UnitId, string Path, string Level);

public sealed record ReviewRunArchiveTarget(
    string UnitId,
    string Name,
    string Path,
    string SubjectHash,
    string? PromptId,
    string? PromptHash,
    string? InputHash);

public sealed record ReviewRunArchiveConfiguration(
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunEstimate? Estimate);

public sealed record ReviewRunArchiveSourceRevision(string? Commit, bool Dirty);

public sealed record ReviewRunArchiveRecord(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    ReviewRunArchiveSubject Subject,
    string Kind,
    IReadOnlyList<ReviewRunArchiveTarget> Targets,
    ReviewRunArchiveConfiguration Configuration,
    ReviewRunArchiveSourceRevision? SourceRevision,
    string? Provenance = null);

public sealed record ReviewRunArchiveGrade(int Score, string Band, string? Rationale);

public sealed record ReviewRunArchiveVerdict(string Type, string Value);

public sealed record ReviewRunArchiveOperation(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    string OperationId,
    int Ordinal,
    int Attempt,
    string UnitId,
    string Path,
    string Level,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? ProviderRunId,
    string? ReviewedHash,
    string? InputHash,
    string? ResultSidecar,
    DateTimeOffset? ReviewedAt,
    ReviewRunArchiveVerdict? Verdict,
    ReviewRunArchiveGrade? Grade,
    string? ErrorCode,
    string? Error);

public sealed record ReviewRunArchiveFindingLocation(
    string Path,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);

public sealed record ReviewRunArchiveFinding(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    string OperationId,
    string Fingerprint,
    string Id,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<ReviewRunArchiveFindingLocation> Locations,
    string State);

public sealed record ReviewRunArchiveAttemptTotals(
    int Operations,
    int Completed,
    int Failed,
    int Skipped,
    TokenUsage Usage,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunArchiveEstimateDeviation(
    decimal? InputTokensPercent,
    decimal? OutputTokensPercent,
    decimal? CostPercent);

public sealed record ReviewRunArchiveQualitySummary(
    int? LowestScore,
    string? LowestGrade,
    string? WorstSecurityVerdict,
    int ActiveFindings,
    string? HighestSeverity);

public sealed record ReviewRunArchiveAttempt(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string RunId,
    int Attempt,
    string Outcome,
    string Completeness,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    DateTimeOffset ArchivedAt,
    ReviewRunArchiveAttemptTotals AttemptTotals,
    ReviewRunArchiveAttemptTotals CumulativeTotals,
    IReadOnlyList<string> ErrorCodes,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunArchiveEstimateDeviation? EstimateDeviation,
    IReadOnlyList<string> LedgerMonths,
    IReadOnlyList<string> OperationIds,
    string? AggregateState,
    ReviewRunArchiveQualitySummary QualitySummary);

public sealed record StoredReviewRunArchive(
    ReviewRunArchiveRecord Run,
    IReadOnlyList<ReviewRunArchiveOperation> Operations,
    IReadOnlyList<ReviewRunArchiveFinding> Findings,
    IReadOnlyList<ReviewRunArchiveAttempt> Attempts);

/// <summary>Owns the tracked, append/create-only historical projection of stopped review attempts.</summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeHistoryPath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions DocumentJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJson = new(JsonSerializerDefaults.Web);
    private readonly string repositoryRoot;
    private readonly string historyPath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        historyPath = Path.Combine(this.repositoryRoot,
            RelativeHistoryPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string HistoryPath => historyPath;

    public void CreateRun(ReviewRunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateEnvelope(run.Schema, ReviewRunArchiveSchemas.Run, run.SchemaVersion, run.RunId);
        if (run.Targets.Count == 0) throw new ArgumentException("An archived run must have at least one target.", nameof(run));
        EnsureConfined(historyPath);
        var directory = Path.Combine(historyPath, run.CreatedAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture), run.RunId);
        EnsureConfined(directory);
        if (TryFindRunDirectory(run.RunId, out _))
            throw new IOException($"Archived review run '{run.RunId}' already exists.");
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(run, DocumentJson) + Environment.NewLine);
    }

    public void AppendOperation(ReviewRunArchiveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateEnvelope(operation.Schema, ReviewRunArchiveSchemas.Operation, operation.SchemaVersion, operation.RunId);
        ValidateIdentifier(operation.OperationId, nameof(operation.OperationId));
        if (operation.Ordinal < 1 || operation.Attempt < 1)
            throw new ArgumentException("Operation ordinal and attempt must be positive.", nameof(operation));
        AppendLine(Path.Combine(FindRunDirectory(operation.RunId), "operations.jsonl"), operation);
    }

    public void AppendFinding(ReviewRunArchiveFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ValidateEnvelope(finding.Schema, ReviewRunArchiveSchemas.Finding, finding.SchemaVersion, finding.RunId);
        ValidateIdentifier(finding.OperationId, nameof(finding.OperationId));
        AppendLine(Path.Combine(FindRunDirectory(finding.RunId), "findings.jsonl"), finding);
    }

    public void CreateAttempt(ReviewRunArchiveAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateEnvelope(attempt.Schema, ReviewRunArchiveSchemas.Attempt, attempt.SchemaVersion, attempt.RunId);
        if (attempt.Attempt is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be between 1 and 9999.");
        var attempts = Path.Combine(FindRunDirectory(attempt.RunId), "attempts");
        EnsureConfined(attempts);
        Directory.CreateDirectory(attempts);
        WriteCreateOnly(Path.Combine(attempts, $"{attempt.Attempt:D4}.json"),
            JsonSerializer.Serialize(attempt, DocumentJson) + Environment.NewLine);
    }

    public StoredReviewRunArchive Load(string runId)
    {
        var directory = FindRunDirectory(runId);
        var run = ReadRequired<ReviewRunArchiveRecord>(Path.Combine(directory, "run.json"));
        ValidateEnvelope(run.Schema, ReviewRunArchiveSchemas.Run, run.SchemaVersion, run.RunId);
        EnsureRunId(runId, run.RunId, "run.json");
        var operations = ReadLines<ReviewRunArchiveOperation>(Path.Combine(directory, "operations.jsonl"), runId,
            operation => operation.RunId);
        var findings = ReadLines<ReviewRunArchiveFinding>(Path.Combine(directory, "findings.jsonl"), runId,
            finding => finding.RunId);
        foreach (var operation in operations)
        {
            ValidateEnvelope(operation.Schema, ReviewRunArchiveSchemas.Operation, operation.SchemaVersion,
                operation.RunId);
            ValidateIdentifier(operation.OperationId, nameof(operation.OperationId));
            if (operation.Ordinal < 1 || operation.Attempt < 1)
                throw new InvalidDataException("Archived operation ordinal and attempt must be positive.");
        }
        foreach (var finding in findings)
        {
            ValidateEnvelope(finding.Schema, ReviewRunArchiveSchemas.Finding, finding.SchemaVersion, finding.RunId);
            ValidateIdentifier(finding.OperationId, nameof(finding.OperationId));
        }
        var attemptsPath = Path.Combine(directory, "attempts");
        var attempts = Directory.Exists(attemptsPath)
            ? Directory.EnumerateFiles(attemptsPath, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Select(path => ReadAttempt(path, runId))
                .ToArray()
            : [];
        return new StoredReviewRunArchive(run, operations, findings, attempts);
    }

    private ReviewRunArchiveAttempt ReadAttempt(string path, string runId)
    {
        var attempt = ReadRequired<ReviewRunArchiveAttempt>(path);
        ValidateEnvelope(attempt.Schema, ReviewRunArchiveSchemas.Attempt, attempt.SchemaVersion, attempt.RunId);
        EnsureRunId(runId, attempt.RunId, Path.GetFileName(path));
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), $"{attempt.Attempt:D4}", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive attempt number disagrees with its file name: {path}");
        return attempt;
    }

    private string FindRunDirectory(string runId)
    {
        ValidateIdentifier(runId, nameof(runId));
        EnsureConfined(historyPath);
        if (!TryFindRunDirectory(runId, out var directory))
            throw new KeyNotFoundException($"Archived review run '{runId}' was not found.");
        return directory!;
    }

    private bool TryFindRunDirectory(string runId, out string? directory)
    {
        ValidateIdentifier(runId, nameof(runId));
        directory = null;
        if (!Directory.Exists(historyPath)) return false;
        foreach (var month in Directory.EnumerateDirectories(historyPath).Order(StringComparer.Ordinal))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(month), "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _)) continue;
            var candidate = Path.Combine(month, runId);
            EnsureConfined(candidate);
            if (!Directory.Exists(candidate)) continue;
            if (directory is not null)
                throw new InvalidDataException($"Archived review run '{runId}' exists in more than one month.");
            directory = candidate;
        }
        return directory is not null;
    }

    private void EnsureConfined(string candidate) => PathConfinement.RejectReparseTraversal(repositoryRoot, candidate);

    private static void ValidateEnvelope(string schema, string expectedSchema, int version, string runId)
    {
        if (!string.Equals(schema, expectedSchema, StringComparison.Ordinal) || version != ReviewRunArchiveSchemas.Version)
            throw new ArgumentException($"Archive record must use schema '{expectedSchema}' version 1.");
        ValidateIdentifier(runId, nameof(runId));
    }

    private static void ValidateIdentifier(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value is "." or ".." || value.Length > 200 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("Archive identifiers may contain only ASCII letters, digits, '.', '_' and '-'.", parameter);
    }

    private static void EnsureRunId(string expected, string actual, string artifact)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive artifact '{artifact}' belongs to run '{actual}', not '{expected}'.");
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), DocumentJson)
        ?? throw new InvalidDataException($"Archive artifact is empty: {path}");

    private static IReadOnlyList<T> ReadLines<T>(string path, string runId, Func<T, string> selectRunId) where T : class
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<T>(line, LineJson)
                         ?? throw new InvalidDataException($"Archive JSONL record is empty: {path}");
            EnsureRunId(runId, selectRunId(record), Path.GetFileName(path));
            records.Add(record);
        }
        return records;
    }

    private static void AppendLine<T>(string path, T record)
    {
        var line = Utf8.GetBytes(JsonSerializer.Serialize(record, LineJson) + "\n");
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
