using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewRunHistorySummary(
    string RunId,
    string? RepositoryId,
    DateTimeOffset? CreatedAt,
    string? Path,
    string? Level,
    string? Kind,
    string? Outcome,
    bool? Complete,
    int? Attempt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int Operations,
    int Findings,
    ReviewRunAttemptQualitySummary? Quality,
    string? ErrorCode = null,
    string? Error = null);

public sealed record ReviewRunHistoryPage(
    IReadOnlyList<ReviewRunHistorySummary> Runs,
    string? NextCursor);

public sealed record ReviewRunHistoryDetail(
    ReviewRunArchiveRecord Run,
    ReviewRunAttemptRecord Attempt,
    IReadOnlyList<ReviewRunOperationRecord> Operations,
    IReadOnlyList<ReviewRunFindingRecord> Findings);

public static class ReviewRunHistoryReader
{
    public static ReviewRunHistoryPage List(
        ReviewRunArchiveStore store,
        string repositoryId,
        string? cursor,
        int limit,
        string? kind,
        string? path,
        string? outcome)
    {
        ArgumentNullException.ThrowIfNull(store);
        var requestedLimit = Math.Clamp(limit, 1, 200);
        var rows = store.LoadAll()
            .Where(result => result.Archive is null ||
                             string.Equals(result.Archive.Run.RepositoryId, repositoryId,
                                 StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(result => result.Archive?.Run.CreatedAt ?? MonthFallback(result.Month))
            .ThenByDescending(result => result.RunId, StringComparer.Ordinal)
            .Select(ToSummary)
            .Where(summary => summary.ErrorCode is not null ||
                              (string.IsNullOrWhiteSpace(kind) || string.Equals(summary.Kind, kind, StringComparison.Ordinal)) &&
                              (string.IsNullOrWhiteSpace(outcome) || string.Equals(summary.Outcome, outcome, StringComparison.Ordinal)) &&
                              (string.IsNullOrWhiteSpace(path) || string.Equals(summary.Path, path, StringComparison.Ordinal)))
            .ToArray();
        var offset = DecodeCursor(cursor, rows);
        var page = rows.Skip(offset).Take(requestedLimit + 1).ToArray();
        return new ReviewRunHistoryPage(
            page.Take(requestedLimit).ToArray(),
            page.Length > requestedLimit ? EncodeCursor(page[requestedLimit - 1].RunId) : null);
    }

    public static ReviewRunHistoryDetail Get(
        ReviewRunArchiveStore store,
        string repositoryId,
        string runId,
        int? attempt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var result = store.LoadAll().FirstOrDefault(candidate =>
            string.Equals(candidate.RunId, runId, StringComparison.Ordinal));
        if (result is null) throw new KeyNotFoundException($"Archived review run '{runId}' was not found.");
        if (result.Archive is null)
            throw new ReviewRunHistoryCorruptException(runId, result.Error ?? "The archive could not be read.");
        if (!string.Equals(result.Archive.Run.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"Archived review run '{runId}' was not found.");
        var selected = attempt.HasValue
            ? result.Archive.Attempts.SingleOrDefault(candidate => candidate.Attempt == attempt.Value)
            : result.Archive.Attempts.OrderByDescending(candidate => candidate.Attempt).FirstOrDefault();
        if (selected is null)
            throw new KeyNotFoundException($"Archived review run '{runId}' has no matching stopped attempt.");
        var operationIds = result.Archive.Operations
            .Where(operation => operation.Attempt <= selected.Attempt)
            .Select(operation => operation.OperationId).ToHashSet(StringComparer.Ordinal);
        return new ReviewRunHistoryDetail(
            result.Archive.Run,
            selected,
            result.Archive.Operations.Where(operation => operationIds.Contains(operation.OperationId)).ToArray(),
            result.Archive.Findings.Where(finding => operationIds.Contains(finding.OperationId)).ToArray());
    }

    public static ReviewRunResponse ToOperationalResponse(StoredReviewRunArchive archive)
    {
        var attempt = archive.Attempts.OrderByDescending(candidate => candidate.Attempt).First();
        var latestByPath = archive.Operations.Where(operation => operation.Level == "file")
            .GroupBy(operation => operation.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(operation => operation.Attempt)
                .ThenByDescending(operation => operation.Ordinal).First(), StringComparer.Ordinal);
        var files = archive.Run.Targets.Select(target =>
        {
            if (!latestByPath.TryGetValue(target.Path, out var operation))
                return new ReviewFileProgress(target.Path, "skipped", null, attempt.FinishedAt, null);
            return new ReviewFileProgress(target.Path, operation.State, operation.StartedAt, operation.FinishedAt,
                operation.Error);
        }).ToArray();
        var aggregateState = archive.Operations.Where(operation => operation.Level != "file")
            .OrderByDescending(operation => operation.Attempt).ThenByDescending(operation => operation.Ordinal)
            .Select(operation => operation.State).FirstOrDefault();
        return new ReviewRunResponse(
            archive.Run.RunId,
            archive.Run.RepositoryId,
            archive.Run.Subject.Path,
            archive.Run.Level,
            archive.Run.Kind,
            archive.Run.Configuration.Model,
            archive.Run.Configuration.ThinkingLevel,
            archive.Run.Configuration.CliType,
            attempt.Outcome,
            attempt.CumulativeCounters.TotalFiles,
            attempt.CumulativeCounters.CompletedFiles,
            attempt.CumulativeCounters.FailedFiles,
            archive.Run.CreatedAt,
            archive.Attempts.Min(candidate => candidate.StartedAt),
            attempt.FinishedAt,
            files,
            archive.Operations.Where(operation => operation.Error is not null).Select(operation => operation.Error!).ToArray(),
            attempt.CumulativeCounters.UsageOperations,
            attempt.CumulativeSpend.Tokens,
            archive.Run.Configuration.Estimate,
            archive.Run.Configuration.TokenCap,
            archive.Run.Configuration.CostCap,
            attempt.CumulativeSpend.Cost,
            attempt.CumulativeSpend.Currency,
            attempt.CumulativeSpend.PriceStatus,
            attempt.CumulativeCounters.SkippedFiles,
            aggregateState,
            null,
            attempt.EstimateDeviation);
    }

    private static ReviewRunHistorySummary ToSummary(ReviewRunArchiveLoadResult result)
    {
        if (result.Archive is null)
            return new ReviewRunHistorySummary(result.RunId, null, null, null, null, null, null, null, null,
                null, null, 0, 0, null, result.ErrorCode, result.Error);
        var attempt = result.Archive.Attempts.OrderByDescending(candidate => candidate.Attempt).FirstOrDefault();
        return new ReviewRunHistorySummary(
            result.Archive.Run.RunId,
            result.Archive.Run.RepositoryId,
            result.Archive.Run.CreatedAt,
            result.Archive.Run.Subject.Path,
            result.Archive.Run.Level,
            result.Archive.Run.Kind,
            attempt?.Outcome,
            attempt?.Complete,
            attempt?.Attempt,
            attempt?.StartedAt,
            attempt?.FinishedAt,
            result.Archive.Operations.Count,
            result.Archive.Findings.Count,
            attempt?.Quality);
    }

    private static DateTimeOffset MonthFallback(string month)
    {
        return DateTimeOffset.TryParse(month + "-01T00:00:00Z", out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;
    }

    private static int DecodeCursor(string? cursor, IReadOnlyList<ReviewRunHistorySummary> rows)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        string runId;
        try
        {
            runId = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') +
                new string('=', (4 - cursor.Length % 4) % 4)));
        }
        catch (FormatException)
        {
            throw new ArgumentException("History cursor is invalid.", nameof(cursor));
        }
        var index = Array.FindIndex(rows.ToArray(), row => string.Equals(row.RunId, runId, StringComparison.Ordinal));
        if (index < 0) throw new ArgumentException("History cursor is no longer available.", nameof(cursor));
        return index + 1;
    }

    private static string EncodeCursor(string runId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(runId)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class ReviewRunHistoryCorruptException(string runId, string error)
    : Exception($"Archived review run '{runId}' is corrupt: {error}");

public static class ReviewRunArchiveMigration
{
    public const string Provenance = "migrated-from-run-store-v0";

    public static void Migrate(
        string repositoryRoot,
        StoredReviewRun stored,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archive = new ReviewRunArchiveStore(repositoryRoot);
        if (!archive.Exists(stored.Manifest.CreatedAt, stored.Manifest.RunId))
        {
            var commit = CoverageSensor.GitValue(repositoryRoot, "rev-parse", "--verify", "HEAD");
            var status = CoverageSensor.GitValue(repositoryRoot, "status", "--porcelain", "--untracked-files=normal");
            archive.CreateRun(ReviewRunArchiveRecord.FromManifest(stored.Manifest,
                new ReviewRunSourceRevision(commit, status is null ? null : status.Length > 0), Provenance));
        }
        else
        {
            var existing = archive.Load(stored.Manifest.CreatedAt, stored.Manifest.RunId);
            if (!string.Equals(existing.Run.Provenance, Provenance, StringComparison.Ordinal)) return;
        }

        var targets = stored.Manifest.Targets.Select((target, index) => (target, ordinal: index + 1))
            .ToDictionary(item => item.target.Path, StringComparer.Ordinal);
        var attemptNumber = Math.Max(1, stored.Status.Attempt);
        foreach (var transition in stored.Progress
                     .Where(transition => transition.State is "done" or "failed" or "cancelled" or "skipped-fresh")
                     .GroupBy(transition => transition.Path, StringComparer.Ordinal).Select(group => group.Last()))
        {
            if (!targets.TryGetValue(transition.Path, out var target)) continue;
            var operationId = transition.OperationId ?? DeterministicOperationId(stored.Manifest.RunId, target.ordinal,
                transition.Path);
            archive.AppendOperation(stored.Manifest.CreatedAt, new ReviewRunOperationRecord
            {
                RunId = stored.Manifest.RunId,
                OperationId = operationId,
                Ordinal = transition.Ordinal ?? target.ordinal,
                Attempt = transition.Attempt ?? attemptNumber,
                UnitId = target.target.Id,
                Path = transition.Path,
                Level = "file",
                State = transition.State,
                StartedAt = (transition.StartedAt ?? stored.Status.StartedAt ?? stored.Manifest.CreatedAt).ToUniversalTime(),
                FinishedAt = (transition.FinishedAt ?? stored.Status.FinishedAt ?? stored.Manifest.CreatedAt).ToUniversalTime(),
                ErrorCode = transition.ErrorCode,
                Error = transition.Error,
            });
        }

        if (ReviewRunStore.IsTerminal(stored.Status.State))
        {
            var migrated = archive.Load(stored.Manifest.CreatedAt, stored.Manifest.RunId);
            if (migrated.Attempts.All(attempt => attempt.Attempt != attemptNumber))
            {
                var finishedAt = (stored.Status.FinishedAt ?? stored.Manifest.CreatedAt).ToUniversalTime();
                var counters = new ReviewRunAttemptCounters(
                    stored.Status.TotalFiles, stored.Status.CompletedFiles, stored.Status.FailedFiles,
                    stored.Status.SkippedFiles, stored.Status.UsageOperations);
                var spend = new ReviewRunAttemptSpend(stored.Status.Usage, stored.Status.CostSpent,
                    stored.Status.Currency, stored.Status.PriceStatus);
                archive.CreateAttempt(stored.Manifest.CreatedAt, new ReviewRunAttemptRecord
                {
                    RunId = stored.Manifest.RunId,
                    Attempt = attemptNumber,
                    Outcome = stored.Status.State,
                    Complete = stored.Status.State == "done",
                    StartedAt = (stored.Status.AttemptStartedAt ?? stored.Status.StartedAt ?? stored.Manifest.CreatedAt)
                        .ToUniversalTime(),
                    FinishedAt = finishedAt,
                    ArchivedAt = DateTimeOffset.UtcNow < finishedAt ? finishedAt : DateTimeOffset.UtcNow,
                    Counters = counters,
                    Spend = spend,
                    CumulativeCounters = counters,
                    CumulativeSpend = spend,
                    ErrorCodes = stored.Status.State == "failed" ? ["migrated-run-failed"] : [],
                    LedgerMonths = LedgerMonths(repositoryRoot, stored.Manifest.RunId),
                    OperationIds = migrated.Operations.Select(operation => operation.OperationId).ToArray(),
                    Estimate = stored.Manifest.Estimate,
                    Quality = new ReviewRunAttemptQualitySummary(null, null, null, 0, null),
                });
            }
        }
    }

    private static string DeterministicOperationId(string runId, int ordinal, string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"run-store-v0\0{runId}\0{ordinal}\0{path}"));
        return "operation-" + Convert.ToHexStringLower(bytes);
    }

    private static IReadOnlyList<string> LedgerMonths(string repositoryRoot, string runId)
    {
        var usagePath = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "usage");
        if (!Directory.Exists(usagePath)) return [];
        var months = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(usagePath, "????-??.jsonl", SearchOption.TopDirectoryOnly))
        {
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("reviewRunId", out var reviewRunId) &&
                        string.Equals(reviewRunId.GetString(), runId, StringComparison.Ordinal))
                        months.Add(Path.GetFileNameWithoutExtension(file));
                }
                catch (JsonException) { }
            }
        }
        return months.Order(StringComparer.Ordinal).ToArray();
    }
}
