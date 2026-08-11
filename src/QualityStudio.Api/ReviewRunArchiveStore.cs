using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class ReviewRunArchiveSchemas
{
    public const string Run = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string Operation = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string Finding = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string Attempt = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";
}

public sealed record ReviewRunSourceRevision(string? Commit, bool Dirty);

public sealed record ReviewRunArchiveConfiguration(
    string? Model,
    string? ThinkingLevel,
    string CliType,
    bool Force,
    bool RouteOverride);

public sealed record ReviewRunArchiveManifest(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string RunId,
    string RepositoryId,
    DateTimeOffset CreatedAt,
    ReviewRunPlanNode Subject,
    string Level,
    string Kind,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    ReviewRunArchiveConfiguration Configuration,
    ReviewRunEstimate? Estimate,
    long? TokenCap,
    decimal? CostCap,
    ReviewRunSourceRevision SourceRevision,
    string Provenance)
{
    public static ReviewRunArchiveManifest From(
        ReviewRunManifest manifest,
        string repositoryRoot,
        string provenance = "native")
    {
        var commit = CoverageSensor.GitValue(repositoryRoot, "rev-parse", "--verify", "HEAD");
        var status = CoverageSensor.GitValue(repositoryRoot, "status", "--porcelain");
        return new ReviewRunArchiveManifest(
            ReviewRunArchiveSchemas.Run,
            1,
            manifest.RunId,
            manifest.RepositoryId,
            manifest.CreatedAt.ToUniversalTime(),
            manifest.Node,
            manifest.Level,
            manifest.Kind,
            manifest.Targets,
            new ReviewRunArchiveConfiguration(
                manifest.Model,
                manifest.ThinkingLevel,
                manifest.CliType,
                manifest.Force,
                manifest.RouteOverride),
            manifest.Estimate,
            manifest.TokenCap,
            manifest.CostCap,
            new ReviewRunSourceRevision(commit, status is { Length: > 0 }),
            provenance);
    }
}

public sealed record ReviewRunArchiveGrade(int Score, string Band, string Rationale);

public sealed record ReviewRunOperationRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
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
    string? ReviewInputsHash,
    string? ResultSidecar,
    string VerdictType,
    string? Verdict,
    ReviewRunArchiveGrade? Grade,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? UsageRecordedAt,
    string? Error);

public sealed record ReviewRunFindingRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string OperationId,
    int Attempt,
    string Fingerprint,
    string FindingId,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<FindingLocation> Locations,
    string StateAtObservation);

public sealed record ReviewRunQualitySummary(
    int? LowestGrade,
    string? LowestBand,
    string? WorstSecurityVerdict,
    int Findings,
    IReadOnlyDictionary<string, int> FindingsBySeverity);

public sealed record ReviewRunAttemptRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    string RunId,
    int Attempt,
    string Outcome,
    bool Complete,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    string? AggregateState,
    IReadOnlyList<string> Errors,
    string? StopReason,
    long? TokenCap,
    decimal? CostCap,
    int UsageOperations,
    TokenUsage Usage,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    IReadOnlyList<string> LedgerMonths,
    IReadOnlyList<string> OperationIds,
    ReviewRunQualitySummary Quality,
    DateTimeOffset ArchivedAt,
    string Provenance = "native");

public sealed record ReviewRunArchiveError(string Code, string RunId, string Detail);

public sealed record ReviewRunHistoryItem(
    string RunId,
    string RepositoryId,
    DateTimeOffset? CreatedAt,
    string? Path,
    string? Level,
    string? Kind,
    string? Model,
    int? Attempt,
    string? Outcome,
    bool? Complete,
    DateTimeOffset? FinishedAt,
    int? Operations,
    int? Findings,
    TokenUsage? Usage,
    decimal? CostSpent,
    string? Currency,
    ReviewRunQualitySummary? Quality,
    ReviewRunArchiveError? Error = null,
    int? TotalFiles = null,
    int? CompletedFiles = null,
    int? FailedFiles = null,
    int? SkippedFiles = null,
    int? UsageOperations = null,
    string? AggregateState = null);

public sealed record ReviewRunHistoryPage(
    IReadOnlyList<ReviewRunHistoryItem> Runs,
    string? NextCursor);

public sealed record ReviewRunHistoryDetail(
    ReviewRunArchiveManifest? Run,
    ReviewRunAttemptRecord? Attempt,
    IReadOnlyList<ReviewRunOperationRecord> Operations,
    IReadOnlyList<ReviewRunFindingRecord> Findings,
    ReviewRunArchiveError? Error = null);

/// <summary>
/// Repository-owned, immutable review-run history. This store owns files only; it never invokes Git mutations.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeHistoryPath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(indented: true);
    private static readonly JsonSerializerOptions LineJsonOptions = CreateJsonOptions(indented: false);
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string repositoryRoot;
    private readonly string historyPath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        historyPath = Path.Combine(this.repositoryRoot,
            RelativeHistoryPath.Replace('/', Path.DirectorySeparatorChar));
        EnsureContained(historyPath, allowRoot: false);
    }

    public string HistoryPath => historyPath;

    public void CreateRun(ReviewRunArchiveManifest manifest)
    {
        ValidateManifest(manifest);
        var directory = RunDirectory(manifest.CreatedAt, manifest.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), Serialize(manifest));
    }

    public void EnsureRun(ReviewRunArchiveManifest manifest)
    {
        ValidateManifest(manifest);
        var path = Path.Combine(RunDirectory(manifest.CreatedAt, manifest.RunId), "run.json");
        lock (FileLock(path))
        {
            if (File.Exists(path))
            {
                var existing = ReadRequired<ReviewRunArchiveManifest>(path);
                if (!string.Equals(existing.RunId, manifest.RunId, StringComparison.Ordinal) ||
                    !string.Equals(existing.RepositoryId, manifest.RepositoryId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Archived run manifest identity mismatch at '{path}'.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteCreateOnly(path, Serialize(manifest));
        }
    }

    public bool AppendOperation(DateTimeOffset createdAt, ReviewRunOperationRecord operation)
    {
        ValidateOperation(operation);
        return AppendUnique(
            Path.Combine(RunDirectory(createdAt, operation.RunId), "operations.jsonl"),
            "operationId",
            operation.OperationId,
            operation);
    }

    public int AppendFindings(DateTimeOffset createdAt, string runId, IEnumerable<ReviewRunFindingRecord> findings)
    {
        var appended = 0;
        foreach (var finding in findings)
        {
            ValidateFinding(finding);
            var identity = $"{finding.OperationId}\0{finding.Fingerprint}";
            if (AppendUnique(Path.Combine(RunDirectory(createdAt, runId), "findings.jsonl"),
                    identityProperty: null, identity, finding))
                appended++;
        }
        return appended;
    }

    public void CreateAttempt(DateTimeOffset createdAt, ReviewRunAttemptRecord attempt)
    {
        ValidateAttempt(attempt);
        var directory = Path.Combine(RunDirectory(createdAt, attempt.RunId), "attempts");
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, $"{attempt.Attempt:0000}.json"), Serialize(attempt));
    }

    public void EnsureAttempt(DateTimeOffset createdAt, ReviewRunAttemptRecord attempt)
    {
        ValidateAttempt(attempt);
        var path = Path.Combine(RunDirectory(createdAt, attempt.RunId), "attempts", $"{attempt.Attempt:0000}.json");
        lock (FileLock(path))
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteCreateOnly(path, Serialize(attempt));
        }
    }

    public IReadOnlyList<ReviewRunOperationRecord> ReadOperations(DateTimeOffset createdAt, string runId) =>
        ReadLines<ReviewRunOperationRecord>(Path.Combine(RunDirectory(createdAt, runId), "operations.jsonl"));

    public IReadOnlyList<ReviewRunFindingRecord> ReadFindings(DateTimeOffset createdAt, string runId) =>
        ReadLines<ReviewRunFindingRecord>(Path.Combine(RunDirectory(createdAt, runId), "findings.jsonl"));

    public IReadOnlyList<ReviewRunAttemptRecord> ReadAttempts(DateTimeOffset createdAt, string runId)
    {
        var directory = Path.Combine(RunDirectory(createdAt, runId), "attempts");
        if (!Directory.Exists(directory)) return [];
        var attempts = new List<ReviewRunAttemptRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "????.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var attempt = ReadRequired<ReviewRunAttemptRecord>(path);
            ValidateAttempt(attempt);
            if (!string.Equals(attempt.RunId, runId, StringComparison.Ordinal))
                throw new InvalidDataException($"Archived attempt identity mismatch at '{path}'.");
            attempts.Add(attempt);
        }
        if (attempts.Select(item => item.Attempt).Distinct().Count() != attempts.Count)
            throw new InvalidDataException($"Archived run '{runId}' contains duplicate attempt numbers.");
        return attempts;
    }

    public ReviewRunHistoryPage Query(
        string repositoryId,
        string? cursor = null,
        int limit = 30,
        string? kind = null,
        string? path = null,
        string? outcome = null,
        IEnumerable<ReviewRunHistoryItem>? supplementalRows = null)
    {
        var requestedLimit = Math.Clamp(limit, 1, 100);
        var directories = EnumerateRunDirectories().ToArray();
        var archived = directories
            .Select(LoadSummary)
            .OfType<ReviewRunHistoryItem>()
            .ToArray();
        var archivedIds = directories.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var rows = archived
            .Concat((supplementalRows ?? []).Where(item => !archivedIds.Contains(item.RunId)))
            .Where(item => item.Error is not null ||
                           string.Equals(item.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Error is not null || string.IsNullOrWhiteSpace(kind) ||
                           string.Equals(item.Kind, kind, StringComparison.Ordinal))
            .Where(item => item.Error is not null || string.IsNullOrWhiteSpace(path) ||
                           string.Equals(item.Path, path, StringComparison.Ordinal) ||
                           item.Path?.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal) == true)
            .Where(item => item.Error is not null || string.IsNullOrWhiteSpace(outcome) ||
                           string.Equals(item.Outcome, outcome, StringComparison.Ordinal))
            .OrderByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.RunId, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorKey = DecodeCursor(cursor);
            var index = rows.FindIndex(row => CursorKey(row) == cursorKey);
            if (index < 0) throw new ArgumentException("The review history cursor is invalid or no longer available.");
            rows = rows.Skip(index + 1).ToList();
        }

        var page = rows.Take(requestedLimit).ToArray();
        return new ReviewRunHistoryPage(page,
            rows.Count > page.Length && page.Length > 0 ? EncodeCursor(CursorKey(page[^1])) : null);
    }

    public static IReadOnlyList<ReviewRunHistoryItem> LegacyUsageOnlyRows(
        string repositoryId,
        IEnumerable<ReviewUsageEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .GroupBy(entry => entry.ReviewRunId ?? entry.RunId, StringComparer.Ordinal)
            .Select(group =>
            {
                var usage = group.ToArray();
                return new ReviewRunHistoryItem(
                    group.Key,
                    repositoryId,
                    null,
                    CommonValue(usage.Select(entry => entry.Path)),
                    CommonValue(usage.Select(entry => entry.Level)),
                    CommonValue(usage.Select(entry => entry.Kind)),
                    CommonValue(usage.Select(entry => entry.Model)),
                    null,
                    "legacy-usage-only",
                    null,
                    null,
                    usage.Length,
                    null,
                    new TokenUsage(
                        SumKnown(usage, entry => entry.Tokens.InputTokens),
                        SumKnown(usage, entry => entry.Tokens.OutputTokens),
                        SumKnown(usage, entry => entry.Tokens.CachedInputTokens),
                        SumKnown(usage, entry => entry.Tokens.ReasoningOutputTokens),
                        usage.Sum(entry => entry.Tokens.DurationMs)),
                    null,
                    null,
                    null,
                    UsageOperations: usage.Length);
            })
            .OrderBy(item => item.RunId, StringComparer.Ordinal)
            .ToArray();
    }

    public ReviewRunHistoryDetail Get(string repositoryId, string runId, int? attempt = null)
    {
        ValidateSegment(runId, nameof(runId));
        string directory;
        try
        {
            directory = FindRunDirectory(runId);
            var manifest = ReadRequired<ReviewRunArchiveManifest>(Path.Combine(directory, "run.json"));
            ValidateManifest(manifest);
            if (!string.Equals(manifest.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                throw new KeyNotFoundException($"Archived review run '{runId}' was not found.");
            var attempts = ReadAttempts(manifest.CreatedAt, runId);
            var selected = attempt.HasValue
                ? attempts.SingleOrDefault(candidate => candidate.Attempt == attempt.Value)
                : attempts.OrderByDescending(candidate => candidate.Attempt).FirstOrDefault();
            if (selected is null)
                throw new KeyNotFoundException($"Archived review run '{runId}' has no matching stopped attempt.");
            var operations = ReadOperations(manifest.CreatedAt, runId)
                .Where(operation => operation.Attempt <= selected.Attempt).ToArray();
            var operationIds = operations.Select(operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
            var findings = ReadFindings(manifest.CreatedAt, runId)
                .Where(finding => finding.Attempt <= selected.Attempt && operationIds.Contains(finding.OperationId)).ToArray();
            return new ReviewRunHistoryDetail(manifest, selected, operations, findings);
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return CorruptDetail(runId, exception);
        }
    }

    public void MigrateFromRunStore(StoredReviewRun stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var manifest = ReviewRunArchiveManifest.From(stored.Manifest, repositoryRoot, "migrated-from-run-store-v0");
        EnsureRun(manifest);
        var attempt = Math.Max(1, stored.Status.Attempt);
        var ordinalByPath = stored.Manifest.Targets.Select((target, index) => (target.Path, Ordinal: index + 1))
            .ToDictionary(item => item.Path, item => item.Ordinal, StringComparer.Ordinal);
        foreach (var transition in stored.Progress
                     .Where(item => item.State is "done" or "failed" or "cancelled" or "skipped-fresh")
                     .GroupBy(item => item.Path, StringComparer.Ordinal)
                     .Select(group => group.Last()))
        {
            var operationId = transition.OperationId ?? StableLegacyOperationId(stored.Manifest.RunId, transition.Path);
            AppendOperation(manifest.CreatedAt, new ReviewRunOperationRecord(
                ReviewRunArchiveSchemas.Operation,
                1,
                stored.Manifest.RunId,
                operationId,
                transition.Ordinal ?? ordinalByPath.GetValueOrDefault(transition.Path),
                transition.Attempt ?? attempt,
                stored.Manifest.Targets.FirstOrDefault(target => target.Path == transition.Path)?.Id ?? "unknown",
                transition.Path,
                "file",
                transition.State,
                transition.StartedAt ?? stored.Status.StartedAt ?? stored.Manifest.CreatedAt,
                transition.FinishedAt ?? stored.Status.FinishedAt ?? stored.Manifest.CreatedAt,
                null,
                null,
                null,
                null,
                "unknown",
                null,
                null,
                null,
                null,
                transition.Error));
        }

        if (ReviewRunStore.IsTerminal(stored.Status.State))
        {
            EnsureAttempt(manifest.CreatedAt, CreateAttemptRecord(
                manifest,
                stored.Status,
                attempt,
                stored.Status.AttemptStartedAt ?? stored.Status.StartedAt ?? stored.Manifest.CreatedAt,
                "migrated-from-run-store-v0"));
        }
    }

    public ReviewRunAttemptRecord CreateAttemptRecord(
        ReviewRunArchiveManifest manifest,
        ReviewRunStatus status,
        int attempt,
        DateTimeOffset attemptStartedAt,
        string provenance = "native")
    {
        var operations = ReadOperations(manifest.CreatedAt, manifest.RunId)
            .Where(operation => operation.Attempt <= attempt).ToArray();
        var operationIds = operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).ToArray();
        var findings = ReadFindings(manifest.CreatedAt, manifest.RunId)
            .Where(finding => operationIds.Contains(finding.OperationId, StringComparer.Ordinal)).ToArray();
        return new ReviewRunAttemptRecord(
            ReviewRunArchiveSchemas.Attempt,
            1,
            manifest.RunId,
            attempt,
            status.State,
            status.State == "done",
            attemptStartedAt.ToUniversalTime(),
            (status.FinishedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            status.TotalFiles,
            status.CompletedFiles,
            status.FailedFiles,
            status.SkippedFiles,
            status.AggregateState,
            status.Errors,
            status.StopReason,
            status.TokenCap,
            status.CostCap,
            status.UsageOperations,
            status.Usage,
            status.CostSpent,
            status.Currency,
            status.PriceStatus,
            LedgerMonths(manifest.CreatedAt, status.FinishedAt ?? DateTimeOffset.UtcNow),
            operationIds,
            QualitySummary(operations, findings),
            DateTimeOffset.UtcNow,
            provenance);
    }

    public static string StableLegacyOperationId(string runId, string path)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes($"{runId}\0{path}")));
        return "operation-" + hash[..32];
    }

    private ReviewRunHistoryItem? LoadSummary(string directory)
    {
        var runId = Path.GetFileName(directory);
        try
        {
            var manifest = ReadRequired<ReviewRunArchiveManifest>(Path.Combine(directory, "run.json"));
            ValidateManifest(manifest);
            var attempts = ReadAttempts(manifest.CreatedAt, manifest.RunId);
            if (attempts.Count == 0) return null;
            var latest = attempts.OrderByDescending(attempt => attempt.Attempt).First();
            return new ReviewRunHistoryItem(
                manifest.RunId,
                manifest.RepositoryId,
                manifest.CreatedAt,
                manifest.Subject.Path,
                manifest.Level,
                manifest.Kind,
                manifest.Configuration.Model,
                latest.Attempt,
                latest.Outcome,
                latest.Complete,
                latest.FinishedAt,
                latest.OperationIds.Count,
                latest.Quality.Findings,
                latest.Usage,
                latest.CostSpent,
                latest.Currency,
                latest.Quality,
                TotalFiles: latest.TotalFiles,
                CompletedFiles: latest.CompletedFiles,
                FailedFiles: latest.FailedFiles,
                SkippedFiles: latest.SkippedFiles,
                UsageOperations: latest.UsageOperations,
                AggregateState: latest.AggregateState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new ReviewRunHistoryItem(
                runId,
                TryReadRepositoryId(directory) ?? string.Empty,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                new ReviewRunArchiveError("history-corrupt", runId, exception.Message));
        }
    }

    private static ReviewRunHistoryDetail CorruptDetail(string runId, Exception exception) =>
        new(null, null, [], [], new ReviewRunArchiveError("history-corrupt", runId, exception.Message));

    private string? TryReadRepositoryId(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "run.json")));
            return document.RootElement.TryGetProperty("repositoryId", out var value) ? value.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateRunDirectories()
    {
        if (!Directory.Exists(historyPath)) return [];
        return Directory.EnumerateDirectories(historyPath, "????-??", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .SelectMany(month => Directory.EnumerateDirectories(month, "*", SearchOption.TopDirectoryOnly))
            .Where(directory => File.Exists(Path.Combine(directory, "run.json")));
    }

    private string FindRunDirectory(string runId)
    {
        var matches = EnumerateRunDirectories()
            .Where(directory => string.Equals(Path.GetFileName(directory), runId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => throw new KeyNotFoundException($"Archived review run '{runId}' was not found."),
            1 => matches[0],
            _ => throw new InvalidDataException($"Archived review run '{runId}' exists in more than one month."),
        };
    }

    private bool AppendUnique<T>(string path, string? identityProperty, string identity, T value)
    {
        lock (FileLock(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    using var document = JsonDocument.Parse(line);
                    var existing = identityProperty is null
                        ? FindingIdentity(document.RootElement)
                        : document.RootElement.GetProperty(identityProperty).GetString();
                    if (string.Equals(existing, identity, StringComparison.Ordinal)) return false;
                }
            }
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(value, LineJsonOptions) + "\n");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
    }

    private static string FindingIdentity(JsonElement root) =>
        $"{root.GetProperty("operationId").GetString()}\0{root.GetProperty("fingerprint").GetString()}";

    private IReadOnlyList<T> ReadLines<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            records.Add(JsonSerializer.Deserialize<T>(line, LineJsonOptions)
                        ?? throw new InvalidDataException($"Archived JSONL record is empty at '{path}'."));
        }
        return records;
    }

    private string RunDirectory(DateTimeOffset createdAt, string runId)
    {
        ValidateSegment(runId, nameof(runId));
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var directory = Path.Combine(historyPath, month, runId);
        EnsureContained(directory, allowRoot: false);
        return directory;
    }

    private void EnsureContained(string candidate, bool allowRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowRoot && string.Equals(root, full, comparison)) return;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Review run archive paths must remain inside the repository root.");
        PathConfinement.RejectReparseTraversal(repositoryRoot, full);
    }

    private static void ValidateManifest(ReviewRunArchiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != 1 || manifest.Schema != ReviewRunArchiveSchemas.Run)
            throw new ArgumentException("Unsupported archived run schema.", nameof(manifest));
        ValidateSegment(manifest.RunId, nameof(manifest.RunId));
        if (string.IsNullOrWhiteSpace(manifest.RepositoryId) || string.IsNullOrWhiteSpace(manifest.Kind) ||
            string.IsNullOrWhiteSpace(manifest.Level) || manifest.CreatedAt.Offset != TimeSpan.Zero ||
            manifest.SourceRevision is null ||
            manifest.SourceRevision.Commit is { } commit && !IsCommitHash(commit))
            throw new ArgumentException("Archived run identity and UTC createdAt are required.", nameof(manifest));
    }

    private static void ValidateOperation(ReviewRunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.SchemaVersion != 1 || operation.Schema != ReviewRunArchiveSchemas.Operation ||
            string.IsNullOrWhiteSpace(operation.OperationId) || operation.Ordinal < 1 || operation.Attempt < 1 ||
            operation.StartedAt.Offset != TimeSpan.Zero || operation.FinishedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Archived operation is invalid.", nameof(operation));
        ValidateSegment(operation.RunId, nameof(operation.RunId));
    }

    private static void ValidateFinding(ReviewRunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.SchemaVersion != 1 || finding.Schema != ReviewRunArchiveSchemas.Finding ||
            string.IsNullOrWhiteSpace(finding.OperationId) || finding.Attempt < 1 ||
            string.IsNullOrWhiteSpace(finding.Fingerprint) || string.IsNullOrWhiteSpace(finding.FindingId))
            throw new ArgumentException("Archived finding is invalid.", nameof(finding));
    }

    private static void ValidateAttempt(ReviewRunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.SchemaVersion != 1 || attempt.Schema != ReviewRunArchiveSchemas.Attempt || attempt.Attempt < 1 ||
            attempt.StartedAt.Offset != TimeSpan.Zero || attempt.FinishedAt.Offset != TimeSpan.Zero ||
            attempt.ArchivedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Archived attempt is invalid.", nameof(attempt));
        ValidateSegment(attempt.RunId, nameof(attempt.RunId));
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) || value is "." or ".." ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("Archive identifiers cannot contain path separators.", parameterName);
    }

    private static IReadOnlyList<string> LedgerMonths(DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        var months = new List<string>();
        var current = new DateTimeOffset(startedAt.Year, startedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(finishedAt.Year, finishedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        while (current <= end)
        {
            months.Add(current.ToString("yyyy-MM", CultureInfo.InvariantCulture));
            current = current.AddMonths(1);
        }
        return months;
    }

    private static ReviewRunQualitySummary QualitySummary(
        IReadOnlyList<ReviewRunOperationRecord> operations,
        IReadOnlyList<ReviewRunFindingRecord> findings)
    {
        var grades = operations.Where(operation => operation.Grade is not null).Select(operation => operation.Grade!).ToArray();
        var lowest = grades.OrderBy(grade => grade.Score).FirstOrDefault();
        var verdictOrder = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["pass"] = 0,
            ["warn"] = 1,
            ["unavailable"] = 2,
            ["block"] = 3,
        };
        var worst = operations.Where(operation => operation.VerdictType == "security" && operation.Verdict is not null)
            .OrderByDescending(operation => verdictOrder.GetValueOrDefault(operation.Verdict!, -1))
            .Select(operation => operation.Verdict)
            .FirstOrDefault();
        return new ReviewRunQualitySummary(
            lowest?.Score,
            lowest?.Band,
            worst,
            findings.Count,
            findings.GroupBy(finding => finding.Severity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
    }

    private static object FileLock(string path) => FileLocks.GetOrAdd(path, _ => new object());

    private static bool IsCommitHash(string value) =>
        value.Length is 40 or 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static string? CommonValue(IEnumerable<string> values)
    {
        var distinct = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static long? SumKnown(
        IEnumerable<ReviewUsageEntry> entries,
        Func<ReviewUsageEntry, long?> selector)
    {
        var values = entries.Select(selector).ToArray();
        return values.Any(value => value.HasValue) ? values.Sum(value => value ?? 0) : null;
    }

    private static string CursorKey(ReviewRunHistoryItem item) =>
        $"{item.CreatedAt?.UtcTicks ?? 0}:{item.RunId}";

    private static string EncodeCursor(string value) => Convert.ToBase64String(Utf8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeCursor(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return Utf8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The review history cursor is malformed.", nameof(value), exception);
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine;

    private static T ReadRequired<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Archived review run file is empty: {path}");

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static JsonSerializerOptions CreateJsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
