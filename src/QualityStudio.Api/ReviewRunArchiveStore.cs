using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QualityStudio.Api;

public static class ReviewRunArchiveSchemas
{
    public const int Version = 1;
    public const string Run = "https://quality.studio/schemas/run-record.v1.schema.json";
    public const string Operation = "https://quality.studio/schemas/run-operation.v1.schema.json";
    public const string Finding = "https://quality.studio/schemas/run-finding.v1.schema.json";
    public const string Attempt = "https://quality.studio/schemas/run-attempt.v1.schema.json";
}

public sealed record ReviewRunArchiveRun(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string RepositoryId,
    [property: JsonPropertyOrder(4)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(5)] ReviewRunArchiveSubject Subject,
    [property: JsonPropertyOrder(6)] string Kind,
    [property: JsonPropertyOrder(7)] IReadOnlyList<ReviewRunArchiveTarget> Targets,
    [property: JsonPropertyOrder(8)] ReviewRunArchiveConfiguration Configuration,
    [property: JsonPropertyOrder(9)] ReviewRunArchiveCap? Cap,
    [property: JsonPropertyOrder(10)] ReviewRunEstimate? Estimate,
    [property: JsonPropertyOrder(11)] ReviewRunArchiveRevision? SourceRevision)
{
    public static ReviewRunArchiveRun Create(
        string runId,
        string repositoryId,
        DateTimeOffset createdAt,
        ReviewRunArchiveSubject subject,
        string kind,
        IReadOnlyList<ReviewRunArchiveTarget> targets,
        ReviewRunArchiveConfiguration configuration,
        ReviewRunArchiveCap? cap = null,
        ReviewRunEstimate? estimate = null,
        ReviewRunArchiveRevision? sourceRevision = null) =>
        new(ReviewRunArchiveSchemas.Run, ReviewRunArchiveSchemas.Version, runId, repositoryId, createdAt,
            subject, kind, targets, configuration, cap, estimate, sourceRevision);
}

public sealed record ReviewRunArchiveSubject(string UnitId, string Path, string Level);

public sealed record ReviewRunArchiveTarget(
    string OperationId,
    int Ordinal,
    string UnitId,
    string Name,
    string Path,
    string SubjectHash);

public sealed record ReviewRunArchiveConfiguration(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool Force,
    IReadOnlyList<string> PromptIdentifiers);

public sealed record ReviewRunArchiveCap(long? TokenLimit, decimal? CostLimit);

public sealed record ReviewRunArchiveRevision(string Commit, bool Dirty);

public sealed record ReviewRunArchiveOperation(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] int Ordinal,
    [property: JsonPropertyOrder(5)] int Attempt,
    [property: JsonPropertyOrder(6)] string UnitId,
    [property: JsonPropertyOrder(7)] string Path,
    [property: JsonPropertyOrder(8)] string Level,
    [property: JsonPropertyOrder(9)] string State,
    [property: JsonPropertyOrder(10)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(11)] DateTimeOffset FinishedAt,
    [property: JsonPropertyOrder(12)] string? ProviderRunId,
    [property: JsonPropertyOrder(13)] string? ReviewedHash,
    [property: JsonPropertyOrder(14)] string? InputHash,
    [property: JsonPropertyOrder(15)] string? ResultSidecar,
    [property: JsonPropertyOrder(16)] ReviewRunArchiveVerdict? Verdict,
    [property: JsonPropertyOrder(17)] ReviewRunArchiveGrade? Grade)
{
    public static ReviewRunArchiveOperation Create(
        string runId,
        string operationId,
        int ordinal,
        int attempt,
        string unitId,
        string path,
        string level,
        string state,
        DateTimeOffset? startedAt,
        DateTimeOffset finishedAt,
        string? providerRunId = null,
        string? reviewedHash = null,
        string? inputHash = null,
        string? resultSidecar = null,
        ReviewRunArchiveVerdict? verdict = null,
        ReviewRunArchiveGrade? grade = null) =>
        new(ReviewRunArchiveSchemas.Operation, ReviewRunArchiveSchemas.Version, runId, operationId,
            ordinal, attempt, unitId, path, level, state, startedAt, finishedAt, providerRunId,
            reviewedHash, inputHash, resultSidecar, verdict, grade);
}

public sealed record ReviewRunArchiveVerdict(string Type, string Value);

public sealed record ReviewRunArchiveGrade(int Score, string Band, string? Rationale);

public sealed record ReviewRunArchiveFinding(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] string Fingerprint,
    [property: JsonPropertyOrder(5)] string FindingId,
    [property: JsonPropertyOrder(6)] string RuleId,
    [property: JsonPropertyOrder(7)] string Severity,
    [property: JsonPropertyOrder(8)] string Title,
    [property: JsonPropertyOrder(9)] IReadOnlyList<ReviewRunArchiveLocation> Locations,
    [property: JsonPropertyOrder(10)] string State)
{
    public static ReviewRunArchiveFinding Create(
        string runId,
        string operationId,
        string fingerprint,
        string findingId,
        string ruleId,
        string severity,
        string title,
        IReadOnlyList<ReviewRunArchiveLocation> locations,
        string state) =>
        new(ReviewRunArchiveSchemas.Finding, ReviewRunArchiveSchemas.Version, runId, operationId,
            fingerprint, findingId, ruleId, severity, title, locations, state);
}

public sealed record ReviewRunArchiveLocation(
    string Path,
    int? StartLine = null,
    int? StartColumn = null,
    int? EndLine = null,
    int? EndColumn = null);

public sealed record ReviewRunArchiveAttempt(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] int Attempt,
    [property: JsonPropertyOrder(4)] string Outcome,
    [property: JsonPropertyOrder(5)] string Completeness,
    [property: JsonPropertyOrder(6)] DateTimeOffset StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset FinishedAt,
    [property: JsonPropertyOrder(8)] DateTimeOffset ArchivedAt,
    [property: JsonPropertyOrder(9)] ReviewRunArchiveCounters Counters,
    [property: JsonPropertyOrder(10)] ReviewRunArchiveCounters CumulativeCounters,
    [property: JsonPropertyOrder(11)] ReviewRunArchiveSpend Spend,
    [property: JsonPropertyOrder(12)] IReadOnlyList<string> ErrorCodes,
    [property: JsonPropertyOrder(13)] ReviewRunArchiveCap? Cap,
    [property: JsonPropertyOrder(14)] ReviewRunArchiveEstimateDeviation? EstimateDeviation,
    [property: JsonPropertyOrder(15)] IReadOnlyList<string> LedgerReferences,
    [property: JsonPropertyOrder(16)] IReadOnlyList<string> OperationIds,
    [property: JsonPropertyOrder(17)] ReviewRunArchiveQualitySummary QualitySummary)
{
    public static ReviewRunArchiveAttempt Create(
        string runId,
        int attempt,
        string outcome,
        string completeness,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        DateTimeOffset archivedAt,
        ReviewRunArchiveCounters counters,
        ReviewRunArchiveCounters cumulativeCounters,
        ReviewRunArchiveSpend spend,
        IReadOnlyList<string> errorCodes,
        ReviewRunArchiveCap? cap,
        ReviewRunArchiveEstimateDeviation? estimateDeviation,
        IReadOnlyList<string> ledgerReferences,
        IReadOnlyList<string> operationIds,
        ReviewRunArchiveQualitySummary qualitySummary) =>
        new(ReviewRunArchiveSchemas.Attempt, ReviewRunArchiveSchemas.Version, runId, attempt, outcome,
            completeness, startedAt, finishedAt, archivedAt, counters, cumulativeCounters, spend,
            errorCodes, cap, estimateDeviation, ledgerReferences, operationIds, qualitySummary);
}

public sealed record ReviewRunArchiveCounters(
    int Total,
    int Completed,
    int Failed,
    int Skipped,
    int Cancelled,
    int UsageOperations);

public sealed record ReviewRunArchiveSpend(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunArchiveEstimateDeviation(
    decimal? InputTokensPercent,
    decimal? OutputTokensPercent,
    decimal? CostPercent);

public sealed record ReviewRunArchiveQualitySummary(
    int? LowestGradeScore,
    string? LowestGradeBand,
    string? WorstSecurityVerdict,
    int ActiveFindings,
    string? HighestActiveSeverity);

public sealed record StoredReviewRunArchive(
    ReviewRunArchiveRun Run,
    IReadOnlyList<ReviewRunArchiveOperation> Operations,
    IReadOnlyList<ReviewRunArchiveFinding> Findings,
    IReadOnlyList<ReviewRunArchiveAttempt> Attempts);

/// <summary>Repository-owned, append/create-only storage for immutable review-run history.</summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeHistoryPath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions LineJsonOptions = CreateJsonOptions(writeIndented: false);
    private readonly object gate = new();
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

    public string RunPath(DateTimeOffset createdAt, string runId) => RunDirectory(createdAt, runId);

    public void CreateRun(ReviewRunArchiveRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Validate(run);
        lock (gate)
        {
            var directory = RunDirectory(run.CreatedAt, run.RunId);
            Directory.CreateDirectory(directory);
            WriteCreateOnly(Path.Combine(directory, "run.json"), Serialize(run));
        }
    }

    public void AppendOperation(DateTimeOffset runCreatedAt, ReviewRunArchiveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Validate(operation);
        AppendLine(runCreatedAt, operation.RunId, "operations.jsonl", SerializeLine(operation));
    }

    public void AppendFinding(DateTimeOffset runCreatedAt, ReviewRunArchiveFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        Validate(finding);
        AppendLine(runCreatedAt, finding.RunId, "findings.jsonl", SerializeLine(finding));
    }

    public void CreateAttempt(DateTimeOffset runCreatedAt, ReviewRunArchiveAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Validate(attempt);
        lock (gate)
        {
            var directory = RequireRunDirectory(runCreatedAt, attempt.RunId);
            var attempts = Path.Combine(directory, "attempts");
            Directory.CreateDirectory(attempts);
            WriteCreateOnly(Path.Combine(attempts, $"{attempt.Attempt:D4}.json"), Serialize(attempt));
        }
    }

    public StoredReviewRunArchive Load(DateTimeOffset createdAt, string runId)
    {
        lock (gate)
        {
            var directory = RequireRunDirectory(createdAt, runId);
            var run = ReadRequired<ReviewRunArchiveRun>(Path.Combine(directory, "run.json"));
            Validate(run);
            if (!string.Equals(run.RunId, runId, StringComparison.Ordinal))
                throw new InvalidDataException($"Archived run id does not match its directory: {directory}");
            var operations = ReadLines<ReviewRunArchiveOperation>(Path.Combine(directory, "operations.jsonl"), Validate);
            var findings = ReadLines<ReviewRunArchiveFinding>(Path.Combine(directory, "findings.jsonl"), Validate);
            var attemptsPath = Path.Combine(directory, "attempts");
            var attempts = Directory.Exists(attemptsPath)
                ? Directory.EnumerateFiles(attemptsPath, "*.json", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.Ordinal)
                    .Select(path => ReadAttempt(path, runId))
                    .ToArray()
                : [];
            if (operations.Any(operation => !string.Equals(operation.RunId, runId, StringComparison.Ordinal)) ||
                findings.Any(finding => !string.Equals(finding.RunId, runId, StringComparison.Ordinal)))
                throw new InvalidDataException($"Archived records disagree about the run id in '{directory}'.");
            return new StoredReviewRunArchive(run, operations, findings, attempts);
        }
    }

    private void AppendLine(DateTimeOffset createdAt, string runId, string fileName, string line)
    {
        lock (gate)
        {
            var directory = RequireRunDirectory(createdAt, runId);
            var path = Path.Combine(directory, fileName);
            var bytes = Utf8.GetBytes(line + "\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private string RequireRunDirectory(DateTimeOffset createdAt, string runId)
    {
        var directory = RunDirectory(createdAt, runId);
        if (!File.Exists(Path.Combine(directory, "run.json")))
            throw new DirectoryNotFoundException($"Archived review run '{runId}' does not exist.");
        return directory;
    }

    private string RunDirectory(DateTimeOffset createdAt, string runId)
    {
        ValidateIdentifier(runId, nameof(runId));
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var directory = Path.GetFullPath(Path.Combine(historyPath, month, runId));
        EnsureUnder(directory, historyPath, "Archive path");
        return directory;
    }

    private void Validate(ReviewRunArchiveRun run)
    {
        ValidateEnvelope(run.Schema, run.SchemaVersion, ReviewRunArchiveSchemas.Run);
        ValidateIdentifier(run.RunId, nameof(run.RunId));
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RepositoryId);
        ValidateRepositoryPath(run.Subject.Path, nameof(run.Subject.Path));
        if (run.Targets.Count == 0) throw new ArgumentException("An archived run must contain at least one target.");
        foreach (var target in run.Targets)
        {
            ValidateIdentifier(target.OperationId, nameof(target.OperationId));
            if (target.Ordinal < 1) throw new ArgumentOutOfRangeException(nameof(target.Ordinal));
            ValidateRepositoryPath(target.Path, nameof(target.Path));
        }
    }

    private void Validate(ReviewRunArchiveOperation operation)
    {
        ValidateEnvelope(operation.Schema, operation.SchemaVersion, ReviewRunArchiveSchemas.Operation);
        ValidateIdentifier(operation.RunId, nameof(operation.RunId));
        ValidateIdentifier(operation.OperationId, nameof(operation.OperationId));
        if (operation.Ordinal < 1) throw new ArgumentOutOfRangeException(nameof(operation.Ordinal));
        if (operation.Attempt < 1) throw new ArgumentOutOfRangeException(nameof(operation.Attempt));
        ValidateRepositoryPath(operation.Path, nameof(operation.Path));
        if (operation.ResultSidecar is not null)
            ValidateRepositoryPath(operation.ResultSidecar, nameof(operation.ResultSidecar));
    }

    private void Validate(ReviewRunArchiveFinding finding)
    {
        ValidateEnvelope(finding.Schema, finding.SchemaVersion, ReviewRunArchiveSchemas.Finding);
        ValidateIdentifier(finding.RunId, nameof(finding.RunId));
        ValidateIdentifier(finding.OperationId, nameof(finding.OperationId));
        foreach (var location in finding.Locations)
            ValidateRepositoryPath(location.Path, nameof(location.Path));
    }

    private static void Validate(ReviewRunArchiveAttempt attempt)
    {
        ValidateEnvelope(attempt.Schema, attempt.SchemaVersion, ReviewRunArchiveSchemas.Attempt);
        ValidateIdentifier(attempt.RunId, nameof(attempt.RunId));
        if (attempt.Attempt is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(attempt.Attempt));
        foreach (var operationId in attempt.OperationIds)
            ValidateIdentifier(operationId, nameof(attempt.OperationIds));
    }

    private void ValidateRepositoryPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || path[0] == '/' || path.Contains('\\') ||
            (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
            throw new ArgumentException("Repository paths must be portable relative paths.", parameterName);
        var resolved = Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnder(resolved, repositoryRoot, "Repository path", parameterName);
    }

    private static void EnsureUnder(string candidate, string root, string label, string? parameterName = null)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException($"{label} escapes its configured root.", parameterName);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200 || value is "." or ".." ||
            value.Any(character => character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("Archive identifiers must be safe file-name components.", parameterName);
    }

    private static void ValidateEnvelope(string schema, int version, string expectedSchema)
    {
        if (version != ReviewRunArchiveSchemas.Version || !string.Equals(schema, expectedSchema, StringComparison.Ordinal))
            throw new JsonException($"Unsupported review-run archive schema '{schema}' version '{version}'.");
    }

    private static ReviewRunArchiveAttempt ReadAttempt(string path, string runId)
    {
        var attempt = ReadRequired<ReviewRunArchiveAttempt>(path);
        Validate(attempt);
        if (!string.Equals(attempt.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileNameWithoutExtension(path), $"{attempt.Attempt:D4}", StringComparison.Ordinal))
            throw new InvalidDataException($"Archived attempt identity does not match its path: {path}");
        return attempt;
    }

    private static IReadOnlyList<T> ReadLines<T>(string path, Action<T> validate)
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<T>(line, LineJsonOptions)
                         ?? throw new InvalidDataException($"Archived JSONL record is empty: {path}");
            validate(record);
            records.Add(record);
        }
        return records;
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Archived review-run file is empty: {path}");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;

    private static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, LineJsonOptions);

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = writeIndented,
        Encoder = JavaScriptEncoder.Default,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
