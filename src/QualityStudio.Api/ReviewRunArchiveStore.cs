using System.Collections.Concurrent;
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

public sealed record ReviewRunArchiveConfiguration(
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunEstimate? Estimate,
    ReviewModelRecommendation? Recommendation = null,
    bool RouteOverride = false);

public sealed record ReviewRunSourceRevision(string? Commit, bool? Dirty);

public sealed record ReviewRunArchiveRecord
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = ReviewRunArchiveSchemas.Run;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = ReviewRunArchiveSchemas.Version;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required string RepositoryId { get; init; }

    [JsonPropertyOrder(4)]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyOrder(5)]
    public required ReviewRunPlanNode Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required string Level { get; init; }

    [JsonPropertyOrder(7)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<ReviewRunPlanTarget> Targets { get; init; }

    [JsonPropertyOrder(9)]
    public required ReviewRunArchiveConfiguration Configuration { get; init; }

    [JsonPropertyOrder(10)]
    public required ReviewRunSourceRevision SourceRevision { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provenance { get; init; }

    public static ReviewRunArchiveRecord FromManifest(
        ReviewRunManifest manifest,
        ReviewRunSourceRevision? sourceRevision = null,
        string? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new ReviewRunArchiveRecord
        {
            RunId = manifest.RunId,
            RepositoryId = manifest.RepositoryId,
            CreatedAt = manifest.CreatedAt.ToUniversalTime(),
            Subject = manifest.Node,
            Level = manifest.Level,
            Kind = manifest.Kind,
            Targets = manifest.Targets,
            Configuration = new ReviewRunArchiveConfiguration(
                manifest.Model, manifest.ThinkingLevel, manifest.CliType, manifest.Force,
                manifest.TokenCap, manifest.CostCap, manifest.Estimate, manifest.Recommendation,
                manifest.RouteOverride),
            SourceRevision = sourceRevision ?? new ReviewRunSourceRevision(null, null),
            Provenance = provenance,
        };
    }
}

public sealed record ReviewRunArchivedGrade(int Score, string Band);

public sealed record ReviewRunTypedVerdict(string Type, string Value);

public sealed record ReviewRunOperationRecord
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = ReviewRunArchiveSchemas.Operation;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = ReviewRunArchiveSchemas.Version;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(4)]
    public required int Ordinal { get; init; }

    [JsonPropertyOrder(5)]
    public required int Attempt { get; init; }

    [JsonPropertyOrder(6)]
    public required string UnitId { get; init; }

    [JsonPropertyOrder(7)]
    public required string Path { get; init; }

    [JsonPropertyOrder(8)]
    public required string Level { get; init; }

    [JsonPropertyOrder(9)]
    public required string State { get; init; }

    [JsonPropertyOrder(10)]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyOrder(11)]
    public required DateTimeOffset FinishedAt { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderRunId { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ReviewedAt { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewedHash { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewInputsHash { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultSidecar { get; init; }

    [JsonPropertyOrder(17), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewRunTypedVerdict? Verdict { get; init; }

    [JsonPropertyOrder(18), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewRunArchivedGrade? Grade { get; init; }

    [JsonPropertyOrder(19), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyOrder(20), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

public sealed record ReviewRunFindingRecord
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = ReviewRunArchiveSchemas.Finding;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = ReviewRunArchiveSchemas.Version;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(4)]
    public required string Fingerprint { get; init; }

    [JsonPropertyOrder(5)]
    public required string FindingId { get; init; }

    [JsonPropertyOrder(6)]
    public required string RuleId { get; init; }

    [JsonPropertyOrder(7)]
    public required string Severity { get; init; }

    [JsonPropertyOrder(8)]
    public required string Title { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<FindingLocation> Locations { get; init; }

    [JsonPropertyOrder(10)]
    public required string State { get; init; }
}

public sealed record ReviewRunAttemptCounters(
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    int UsageOperations);

public sealed record ReviewRunAttemptSpend(
    TokenUsage Tokens,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunAttemptQualitySummary(
    int? LowestGrade,
    string? LowestBand,
    string? WorstSecurityVerdict,
    int ActiveFindings,
    string? HighestActiveSeverity);

public sealed record ReviewRunAttemptRecord
{
    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = ReviewRunArchiveSchemas.Attempt;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = ReviewRunArchiveSchemas.Version;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required int Attempt { get; init; }

    [JsonPropertyOrder(4)]
    public required string Outcome { get; init; }

    [JsonPropertyOrder(5)]
    public required bool Complete { get; init; }

    [JsonPropertyOrder(6)]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyOrder(7)]
    public required DateTimeOffset FinishedAt { get; init; }

    [JsonPropertyOrder(8)]
    public required DateTimeOffset ArchivedAt { get; init; }

    [JsonPropertyOrder(9)]
    public required ReviewRunAttemptCounters Counters { get; init; }

    [JsonPropertyOrder(10)]
    public required ReviewRunAttemptSpend Spend { get; init; }

    [JsonPropertyOrder(11)]
    public required ReviewRunAttemptCounters CumulativeCounters { get; init; }

    [JsonPropertyOrder(12)]
    public required ReviewRunAttemptSpend CumulativeSpend { get; init; }

    [JsonPropertyOrder(13)]
    public required IReadOnlyList<string> ErrorCodes { get; init; }

    [JsonPropertyOrder(14)]
    public required IReadOnlyList<string> LedgerMonths { get; init; }

    [JsonPropertyOrder(15)]
    public required IReadOnlyList<string> OperationIds { get; init; }

    [JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewRunEstimate? Estimate { get; init; }

    [JsonPropertyOrder(17), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewEstimateDeviation? EstimateDeviation { get; init; }

    [JsonPropertyOrder(18)]
    public required ReviewRunAttemptQualitySummary Quality { get; init; }
}

public sealed record StoredReviewRunArchive(
    ReviewRunArchiveRecord Run,
    IReadOnlyList<ReviewRunOperationRecord> Operations,
    IReadOnlyList<ReviewRunFindingRecord> Findings,
    IReadOnlyList<ReviewRunAttemptRecord> Attempts);

public sealed record ReviewRunArchiveLoadResult(
    string RunId,
    string Month,
    StoredReviewRunArchive? Archive,
    string? ErrorCode,
    string? Error);

/// <summary>Writes and reads tracked, immutable review-run history without owning Git operations.</summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeHistoryPath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions DocumentJsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions LineJsonOptions = CreateJsonOptions(writeIndented: false);
    private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Levels = ["project", "module", "namespace", "file"];
    private static readonly HashSet<string> Kinds = ["code", "security", "performance"];
    private static readonly HashSet<string> OperationStates = ["done", "failed", "cancelled", "skipped-fresh"];
    private static readonly HashSet<string> AttemptOutcomes = ["done", "failed", "cancelled", "capped"];
    private static readonly HashSet<string> FindingSeverities = ["critical", "high", "medium", "low", "info"];
    private static readonly HashSet<string> FindingStates = ["open", "accepted", "waived", "false-positive", "resolved"];
    private static readonly HashSet<string> GradeBands = ["A", "B", "C", "D", "F"];
    private static readonly HashSet<string> SecurityVerdicts = ["pass", "warn", "block", "unavailable"];
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

    public bool Exists(DateTimeOffset runCreatedAt, string runId) =>
        File.Exists(Path.Combine(RunDirectory(runCreatedAt, runId), "run.json"));

    public void CreateRun(ReviewRunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateDocument(run.Schema, ReviewRunArchiveSchemas.Run, run.SchemaVersion);
        ValidateRun(run);
        var directory = RunDirectory(run.CreatedAt, run.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), SerializeDocument(run));
    }

    public void AppendOperation(DateTimeOffset runCreatedAt, ReviewRunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateDocument(operation.Schema, ReviewRunArchiveSchemas.Operation, operation.SchemaVersion);
        ValidateOperation(operation);
        AppendUniqueLine(ExistingRunPath(runCreatedAt, operation.RunId, "operations.jsonl"), operation,
            existing => existing.OperationId);
    }

    public void AppendFinding(DateTimeOffset runCreatedAt, ReviewRunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ValidateDocument(finding.Schema, ReviewRunArchiveSchemas.Finding, finding.SchemaVersion);
        ValidateFinding(finding);
        AppendUniqueLine(ExistingRunPath(runCreatedAt, finding.RunId, "findings.jsonl"), finding,
            existing => existing.OperationId + "\0" + existing.Fingerprint);
    }

    public void CreateAttempt(DateTimeOffset runCreatedAt, ReviewRunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ValidateDocument(attempt.Schema, ReviewRunArchiveSchemas.Attempt, attempt.SchemaVersion);
        ValidateAttempt(attempt);
        var directory = ExistingRunDirectory(runCreatedAt, attempt.RunId);
        var attempts = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attempts);
        WriteCreateOnly(Path.Combine(attempts, $"{attempt.Attempt:0000}.json"), SerializeDocument(attempt));
    }

    public StoredReviewRunArchive Load(DateTimeOffset runCreatedAt, string runId)
    {
        var directory = ExistingRunDirectory(runCreatedAt, runId);
        var run = ReadRequired<ReviewRunArchiveRecord>(Path.Combine(directory, "run.json"));
        ValidateDocument(run.Schema, ReviewRunArchiveSchemas.Run, run.SchemaVersion);
        ValidateRun(run);
        if (!string.Equals(run.RunId, runId, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive run id does not match its directory: '{directory}'.");

        var operations = ReadLines<ReviewRunOperationRecord>(Path.Combine(directory, "operations.jsonl"));
        var findings = ReadLines<ReviewRunFindingRecord>(Path.Combine(directory, "findings.jsonl"));
        var attemptsDirectory = Path.Combine(directory, "attempts");
        var attempts = !Directory.Exists(attemptsDirectory)
            ? []
            : Directory.EnumerateFiles(attemptsDirectory, "????.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Select(ReadRequired<ReviewRunAttemptRecord>)
                .ToArray();

        foreach (var operation in operations)
        {
            ValidateDocument(operation.Schema, ReviewRunArchiveSchemas.Operation, operation.SchemaVersion);
            ValidateOperation(operation);
            EnsureRunId(runId, operation.RunId, directory);
        }
        foreach (var finding in findings)
        {
            ValidateDocument(finding.Schema, ReviewRunArchiveSchemas.Finding, finding.SchemaVersion);
            ValidateFinding(finding);
            EnsureRunId(runId, finding.RunId, directory);
        }
        foreach (var attempt in attempts)
        {
            ValidateDocument(attempt.Schema, ReviewRunArchiveSchemas.Attempt, attempt.SchemaVersion);
            ValidateAttempt(attempt);
            EnsureRunId(runId, attempt.RunId, directory);
            var expectedName = $"{attempt.Attempt:0000}.json";
            if (!File.Exists(Path.Combine(attemptsDirectory, expectedName)))
                throw new InvalidDataException($"Archive attempt number does not match its file in '{directory}'.");
        }

        if (operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() != operations.Count)
            throw new InvalidDataException($"Archive contains duplicate operation ids in '{directory}'.");
        if (attempts.Select(attempt => attempt.Attempt).Distinct().Count() != attempts.Count())
            throw new InvalidDataException($"Archive contains duplicate attempt numbers in '{directory}'.");
        return new StoredReviewRunArchive(run, operations, findings, attempts);
    }

    public IReadOnlyList<ReviewRunArchiveLoadResult> LoadAll()
    {
        PathConfinement.RejectReparseTraversal(repositoryRoot, historyPath);
        if (!Directory.Exists(historyPath)) return [];
        var results = new List<ReviewRunArchiveLoadResult>();
        foreach (var monthDirectory in Directory.EnumerateDirectories(historyPath)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var month = Path.GetFileName(monthDirectory);
            if (month.Length != 7 || month[4] != '-' ||
                !int.TryParse(month.AsSpan(0, 4), out _) || !int.TryParse(month.AsSpan(5, 2), out var monthNumber) ||
                monthNumber is < 1 or > 12)
                continue;
            foreach (var directory in Directory.EnumerateDirectories(monthDirectory).Order(StringComparer.Ordinal))
            {
                var runId = Path.GetFileName(directory);
                try
                {
                    ValidateRunId(runId);
                    var run = ReadRequired<ReviewRunArchiveRecord>(Path.Combine(directory, "run.json"));
                    if (!string.Equals(run.CreatedAt.UtcDateTime.ToString("yyyy-MM"), month, StringComparison.Ordinal))
                        throw new InvalidDataException($"Archive creation month does not match its directory: '{directory}'.");
                    results.Add(new ReviewRunArchiveLoadResult(runId, month, Load(run.CreatedAt, runId), null, null));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   InvalidDataException or ArgumentException)
                {
                    results.Add(new ReviewRunArchiveLoadResult(runId, month, null, "history-corrupt", exception.Message));
                }
            }
        }
        return results;
    }

    private string ExistingRunPath(DateTimeOffset createdAt, string runId, string fileName) =>
        Path.Combine(ExistingRunDirectory(createdAt, runId), fileName);

    private string ExistingRunDirectory(DateTimeOffset createdAt, string runId)
    {
        var directory = RunDirectory(createdAt, runId);
        if (!File.Exists(Path.Combine(directory, "run.json")))
            throw new DirectoryNotFoundException($"Review run archive '{runId}' does not exist.");
        return directory;
    }

    private string RunDirectory(DateTimeOffset createdAt, string runId)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        var directory = Path.GetFullPath(Path.Combine(historyPath, month, runId));
        var confinedPrefix = Path.TrimEndingDirectorySeparator(historyPath) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(confinedPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The review run archive path escapes its repository root.", nameof(runId));
        PathConfinement.RejectReparseTraversal(repositoryRoot, directory);
        return directory;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > 200 || !string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            runId is "." or "..")
            throw new ArgumentException("A review run id must be one safe path component.", nameof(runId));
    }

    private static void ValidateOperation(ReviewRunOperationRecord operation)
    {
        ValidateRunId(operation.RunId);
        if (!IsId(operation.OperationId) || operation.Ordinal < 1 || operation.Attempt < 1 ||
            !IsId(operation.UnitId) || string.IsNullOrWhiteSpace(operation.Path) ||
            !Levels.Contains(operation.Level) || !OperationStates.Contains(operation.State) ||
            operation.FinishedAt < operation.StartedAt || operation.StartedAt.Offset != TimeSpan.Zero ||
            operation.FinishedAt.Offset != TimeSpan.Zero ||
            operation.ProviderRunId is not null && !IsId(operation.ProviderRunId) ||
            operation.ReviewedAt is not null && operation.ReviewedAt.Value.Offset != TimeSpan.Zero ||
            operation.ReviewedHash is not null && string.IsNullOrWhiteSpace(operation.ReviewedHash) ||
            operation.ReviewInputsHash is not null && string.IsNullOrWhiteSpace(operation.ReviewInputsHash) ||
            operation.ResultSidecar is not null && string.IsNullOrWhiteSpace(operation.ResultSidecar) ||
            operation.Verdict is not null && (string.IsNullOrWhiteSpace(operation.Verdict.Type) ||
                                               string.IsNullOrWhiteSpace(operation.Verdict.Value)) ||
            operation.Grade is not null && (operation.Grade.Score is < 0 or > 100 ||
                                             !GradeBands.Contains(operation.Grade.Band)) ||
            operation.ErrorCode is { Length: > 200 } || operation.Error is { Length: > 4000 })
            throw new ArgumentException("The archive operation record is invalid.", nameof(operation));
    }

    private static void ValidateFinding(ReviewRunFindingRecord finding)
    {
        ValidateRunId(finding.RunId);
        if (!IsId(finding.OperationId) || !IsFingerprint(finding.Fingerprint) ||
            !IsId(finding.FindingId) || !IsId(finding.RuleId) ||
            !FindingSeverities.Contains(finding.Severity) || string.IsNullOrWhiteSpace(finding.Title) ||
            !FindingStates.Contains(finding.State) || finding.Locations is null || finding.Locations.Count == 0 ||
            finding.Locations.Any(location => location is null || string.IsNullOrWhiteSpace(location.Path)))
            throw new ArgumentException("The archive finding record is invalid.", nameof(finding));
    }

    private static void ValidateRun(ReviewRunArchiveRecord run)
    {
        ValidateRunId(run.RunId);
        if (!IsId(run.RepositoryId) || run.CreatedAt.Offset != TimeSpan.Zero || run.Subject is null ||
            !IsId(run.Subject.Id) || string.IsNullOrWhiteSpace(run.Subject.Name) ||
            string.IsNullOrWhiteSpace(run.Subject.Path) || !Levels.Contains(run.Level) || !Kinds.Contains(run.Kind) ||
            run.Targets is null || run.Targets.Count == 0 || run.Targets.Any(target => target is null ||
                !IsId(target.Id) || string.IsNullOrWhiteSpace(target.Name) || string.IsNullOrWhiteSpace(target.Path) ||
                string.IsNullOrWhiteSpace(target.SubjectHash)) || run.Configuration is null ||
            string.IsNullOrWhiteSpace(run.Configuration.CliType) || run.Configuration.CliType.Length > 100 ||
            run.Configuration.TokenCap is <= 0 || run.Configuration.CostCap is <= 0 || run.SourceRevision is null ||
            run.Configuration.Recommendation is not null &&
                (!IsId(run.Configuration.Recommendation.PolicyVersion) ||
                 !IsId(run.Configuration.Recommendation.RecommendedModel) ||
                 !IsId(run.Configuration.Recommendation.RecommendedThinkingLevel) ||
                 !IsId(run.Configuration.Recommendation.CapabilityTier) ||
                 !IsId(run.Configuration.Recommendation.CorrectnessFloor) ||
                 string.IsNullOrWhiteSpace(run.Configuration.Recommendation.Reason) ||
                 string.IsNullOrWhiteSpace(run.Configuration.Recommendation.SelectionSource)) ||
            run.SourceRevision.Commit is not null && !IsCommit(run.SourceRevision.Commit) ||
            run.Provenance is not null && (string.IsNullOrWhiteSpace(run.Provenance) || run.Provenance.Length > 200))
            throw new ArgumentException("The archive run record is invalid.", nameof(run));
    }

    private static void ValidateAttempt(ReviewRunAttemptRecord attempt)
    {
        ValidateRunId(attempt.RunId);
        if (attempt.Attempt is < 1 or > 9999 || !AttemptOutcomes.Contains(attempt.Outcome) ||
            attempt.FinishedAt < attempt.StartedAt || attempt.ArchivedAt < attempt.FinishedAt ||
            attempt.StartedAt.Offset != TimeSpan.Zero || attempt.FinishedAt.Offset != TimeSpan.Zero ||
            attempt.ArchivedAt.Offset != TimeSpan.Zero || attempt.Counters is null || attempt.Spend is null ||
            attempt.CumulativeCounters is null || attempt.CumulativeSpend is null || attempt.ErrorCodes is null ||
            attempt.LedgerMonths is null || attempt.OperationIds is null || attempt.Quality is null ||
            !ValidCounters(attempt.Counters) || !ValidCounters(attempt.CumulativeCounters) ||
            !ValidSpend(attempt.Spend) || !ValidSpend(attempt.CumulativeSpend) ||
            attempt.ErrorCodes.Any(string.IsNullOrWhiteSpace) ||
            attempt.LedgerMonths.Any(month => !ValidMonth(month)) ||
            attempt.LedgerMonths.Distinct(StringComparer.Ordinal).Count() != attempt.LedgerMonths.Count ||
            attempt.OperationIds.Any(operationId => !IsId(operationId)) ||
            attempt.OperationIds.Distinct(StringComparer.Ordinal).Count() != attempt.OperationIds.Count ||
            attempt.Quality.LowestGrade is < 0 or > 100 ||
            attempt.Quality.LowestBand is not null && !GradeBands.Contains(attempt.Quality.LowestBand) ||
            attempt.Quality.WorstSecurityVerdict is not null &&
                !SecurityVerdicts.Contains(attempt.Quality.WorstSecurityVerdict) ||
            attempt.Quality.ActiveFindings < 0 || attempt.Quality.HighestActiveSeverity is not null &&
                !FindingSeverities.Contains(attempt.Quality.HighestActiveSeverity))
            throw new ArgumentException("The archive attempt record is invalid.", nameof(attempt));
    }

    private static bool ValidCounters(ReviewRunAttemptCounters counters) =>
        counters.TotalFiles >= 0 && counters.CompletedFiles >= 0 && counters.FailedFiles >= 0 &&
        counters.SkippedFiles >= 0 && counters.UsageOperations >= 0;

    private static bool ValidSpend(ReviewRunAttemptSpend spend) => spend.Tokens is not null &&
        spend.Tokens.InputTokens is null or >= 0 && spend.Tokens.OutputTokens is null or >= 0 &&
        spend.Tokens.CachedInputTokens is null or >= 0 && spend.Tokens.ReasoningOutputTokens is null or >= 0 &&
        spend.Tokens.DurationMs >= 0 && spend.Cost is null or >= 0 && !string.IsNullOrWhiteSpace(spend.PriceStatus);

    private static bool IsId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200;

    private static bool IsFingerprint(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCommit(string value) => value.Length is >= 40 and <= 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidMonth(string month) => month.Length == 7 && month[4] == '-' &&
        int.TryParse(month.AsSpan(0, 4), out _) && int.TryParse(month.AsSpan(5, 2), out var number) &&
        number is >= 1 and <= 12;

    private static void EnsureRunId(string expected, string actual, string directory)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive record has the wrong run id in '{directory}'.");
    }

    private static void ValidateDocument(string schema, string expectedSchema, int version)
    {
        if (version != ReviewRunArchiveSchemas.Version || !string.Equals(schema, expectedSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported archive schema '{schema}' version '{version}'.");
    }

    private static string SerializeDocument<T>(T value) =>
        JsonSerializer.Serialize(value, DocumentJsonOptions) + Environment.NewLine;

    private static void AppendLine<T>(string path, T value)
    {
        var gate = FileGates.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(value, LineJsonOptions) + "\n");
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
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void AppendUniqueLine<T>(string path, T value, Func<T, string> key)
    {
        var gate = FileGates.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            var serialized = JsonSerializer.Serialize(value, LineJsonOptions);
            foreach (var existing in ReadLines<T>(path).Where(existing =>
                         string.Equals(key(existing), key(value), StringComparison.Ordinal)))
            {
                if (string.Equals(JsonSerializer.Serialize(existing, LineJsonOptions), serialized, StringComparison.Ordinal))
                    return;
                throw new InvalidDataException($"Archive identity '{key(value)}' already has a different record in '{path}'.");
            }
            AppendLine(path, value);
        }
    }

    private static IReadOnlyList<T> ReadLines<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                records.Add(JsonSerializer.Deserialize<T>(line, LineJsonOptions)
                    ?? throw new JsonException("Archive line is empty."));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Archive line {lineNumber} is corrupt: {path}", exception);
            }
        }
        return records;
    }

    private static T ReadRequired<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), DocumentJsonOptions)
                ?? throw new JsonException("Archive document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Archive document is corrupt: {path}", exception);
        }
    }

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                throw new JsonException("Archive timestamps must be UTC ISO 8601 values ending in 'Z'.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero)
                throw new JsonException("Archive timestamps must be UTC.");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}
