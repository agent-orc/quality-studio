using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewRunComparability(
    IReadOnlyList<string> Labels,
    string RenameCorrelation);

public sealed record ReviewRunTargetChange(
    string Id,
    string Path,
    string? BeforeHash,
    string? AfterHash);

public sealed record ReviewRunScopeDelta(
    IReadOnlyList<ReviewRunTargetChange> Added,
    IReadOnlyList<ReviewRunTargetChange> Removed,
    IReadOnlyList<ReviewRunTargetChange> Persisting,
    IReadOnlyList<ReviewRunTargetChange> ChangedHashes);

public sealed record ReviewRunInputDelta(
    string UnitId,
    string Path,
    string? BeforeHash,
    string? AfterHash);

public sealed record ReviewRunExecutionSnapshot(
    string Outcome,
    bool Complete,
    int Attempt,
    int FailedFiles,
    int SkippedFiles,
    long DurationMs,
    long? TokenCap,
    decimal? CostCap,
    ReviewEstimateDeviation? EstimateDeviation);

public sealed record ReviewRunExecutionDelta(
    ReviewRunExecutionSnapshot Before,
    ReviewRunExecutionSnapshot After,
    int FailedFilesChange,
    int SkippedFilesChange,
    long DurationMsChange);

public sealed record ReviewRunVerdictDelta(
    string UnitId,
    string Path,
    string Type,
    string? Before,
    string? After);

public sealed record ReviewRunPersistingFindingChange(
    string Identity,
    string BeforeFingerprint,
    string AfterFingerprint,
    string? BeforeSeverity,
    string? AfterSeverity,
    string? BeforeState,
    string? AfterState,
    bool Renamed);

public sealed record ReviewRunEconomySnapshot(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunEconomyDelta(
    ReviewRunEconomySnapshot Before,
    ReviewRunEconomySnapshot After,
    long? InputTokensChange,
    long? OutputTokensChange,
    long? CachedInputTokensChange,
    long? ReasoningOutputTokensChange,
    long DurationMsChange,
    decimal? CostChange);

public sealed record ReviewRunDiff(
    string BeforeRunId,
    int BeforeAttempt,
    string AfterRunId,
    int AfterAttempt,
    ReviewRunComparability Comparability,
    ReviewRunScopeDelta Scope,
    IReadOnlyList<ReviewRunInputDelta> Inputs,
    ReviewRunExecutionDelta Execution,
    IReadOnlyList<UnitGradeDelta> Grades,
    IReadOnlyList<ReviewRunVerdictDelta> Verdicts,
    FindingDelta Findings,
    IReadOnlyList<ReviewRunPersistingFindingChange> FindingChanges,
    ReviewRunEconomyDelta Economy);

public static class ReviewRunDiffService
{
    public static async Task<ReviewRunDiff> CompareAsync(
        string repositoryRoot,
        ReviewRunHistoryDetail before,
        ReviewRunHistoryDetail after,
        bool allowScopeChange = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (!string.Equals(before.Run.RepositoryId, after.Run.RepositoryId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Review runs from different repositories cannot be compared.");
        if (!string.Equals(before.Run.Kind, after.Run.Kind, StringComparison.Ordinal))
            throw new ArgumentException("Review runs with different review kinds cannot be compared.");
        var rootChanged = !string.Equals(before.Run.Subject.Id, after.Run.Subject.Id, StringComparison.Ordinal) ||
                          !string.Equals(before.Run.Subject.Path, after.Run.Subject.Path, StringComparison.Ordinal);
        if (rootChanged && !allowScopeChange)
            throw new ArgumentException("Review runs with different root units require allowScopeChange=true.");

        var (renameMap, renameStatus) = await RenameMapAsync(repositoryRoot, before.Run.SourceRevision.Commit,
            after.Run.SourceRevision.Commit, cancellationToken).ConfigureAwait(false);
        var beforeTargets = before.Run.Targets.ToDictionary(target => Translate(target.Path, renameMap), StringComparer.Ordinal);
        var afterTargets = after.Run.Targets.ToDictionary(target => target.Path, StringComparer.Ordinal);
        var added = afterTargets.Keys.Except(beforeTargets.Keys, StringComparer.Ordinal)
            .Select(path => TargetChange(afterTargets[path], null, afterTargets[path].SubjectHash)).ToArray();
        var removed = beforeTargets.Keys.Except(afterTargets.Keys, StringComparer.Ordinal)
            .Select(path => TargetChange(beforeTargets[path], beforeTargets[path].SubjectHash, null)).ToArray();
        var persisting = beforeTargets.Keys.Intersect(afterTargets.Keys, StringComparer.Ordinal)
            .Select(path => new ReviewRunTargetChange(afterTargets[path].Id, path,
                beforeTargets[path].SubjectHash, afterTargets[path].SubjectHash)).ToArray();
        var changedHashes = persisting.Where(target =>
            !string.Equals(target.BeforeHash, target.AfterHash, StringComparison.Ordinal)).ToArray();

        var beforeOperations = LatestOperations(before, renameMap);
        var afterOperations = LatestOperations(after, null);
        var inputs = beforeOperations.Keys.Union(afterOperations.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(path =>
            {
                beforeOperations.TryGetValue(path, out var beforeOperation);
                afterOperations.TryGetValue(path, out var afterOperation);
                return new ReviewRunInputDelta(afterOperation?.UnitId ?? beforeOperation!.UnitId, path,
                    beforeOperation?.ReviewInputsHash, afterOperation?.ReviewInputsHash);
            })
            .Where(delta => !string.Equals(delta.BeforeHash, delta.AfterHash, StringComparison.Ordinal))
            .ToArray();

        var labels = new List<string>();
        if (rootChanged || added.Length > 0 || removed.Length > 0) labels.Add("scope-changed");
        if (inputs.Length > 0) labels.Add("policy-changed");
        if (!string.Equals(before.Run.Configuration.Model, after.Run.Configuration.Model, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.Run.Configuration.ThinkingLevel, after.Run.Configuration.ThinkingLevel,
                StringComparison.OrdinalIgnoreCase))
            labels.Add("model-changed");
        if (!before.Attempt.Complete || !after.Attempt.Complete) labels.Add("incomplete");
        if (labels.Count == 0) labels.Add("exact");

        var grades = GradeDeltas(before.Run.Kind, beforeOperations, afterOperations);
        var verdicts = VerdictDeltas(beforeOperations, afterOperations);
        var (findings, findingChanges) = FindingDeltas(before, after, beforeOperations, afterOperations, renameMap);
        return new ReviewRunDiff(
            before.Run.RunId,
            before.Attempt.Attempt,
            after.Run.RunId,
            after.Attempt.Attempt,
            new ReviewRunComparability(labels, renameStatus),
            new ReviewRunScopeDelta(added, removed, persisting, changedHashes),
            inputs,
            ExecutionDelta(before, after),
            grades,
            verdicts,
            findings,
            findingChanges,
            EconomyDelta(before.Attempt, after.Attempt));
    }

    private static IReadOnlyList<UnitGradeDelta> GradeDeltas(
        string kind,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> before,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
        {
            before.TryGetValue(path, out var left);
            after.TryGetValue(path, out var right);
            var beforeGrade = left?.Grade is null ? null : new GradeSnapshot(left.Grade.Score, left.Grade.Band);
            var afterGrade = right?.Grade is null ? null : new GradeSnapshot(right.Grade.Score, right.Grade.Band);
            var scoreChange = beforeGrade is not null && afterGrade is not null
                ? afterGrade.Score - beforeGrade.Score
                : (int?)null;
            return new UnitGradeDelta(right?.UnitId ?? left!.UnitId, path, kind, beforeGrade, afterGrade,
                scoreChange, scoreChange < 0);
        }).Where(delta => delta.Before is not null || delta.After is not null).ToArray();

    private static IReadOnlyList<ReviewRunVerdictDelta> VerdictDeltas(
        IReadOnlyDictionary<string, ReviewRunOperationRecord> before,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
        {
            before.TryGetValue(path, out var left);
            after.TryGetValue(path, out var right);
            var type = right?.Verdict?.Type ?? left?.Verdict?.Type;
            return type is null
                ? null
                : new ReviewRunVerdictDelta(right?.UnitId ?? left!.UnitId, path, type,
                    left?.Verdict?.Value, right?.Verdict?.Value);
        }).Where(delta => delta is not null).Cast<ReviewRunVerdictDelta>().ToArray();

    private static (FindingDelta Delta, IReadOnlyList<ReviewRunPersistingFindingChange> Changes) FindingDeltas(
        ReviewRunHistoryDetail before,
        ReviewRunHistoryDetail after,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> beforeOperations,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> afterOperations,
        IReadOnlyDictionary<string, string> renameMap)
    {
        var left = before.Findings.GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var right = after.Findings.GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var pairs = new List<(ReviewRunFindingRecord Before, ReviewRunFindingRecord After, bool Renamed)>();
        foreach (var fingerprint in left.Keys.Intersect(right.Keys, StringComparer.Ordinal))
        {
            pairs.Add((left[fingerprint], right[fingerprint], false));
            left.Remove(fingerprint);
            right.Remove(fingerprint);
        }
        foreach (var oldFinding in left.Values.ToArray())
        {
            var oldPath = PrimaryPath(oldFinding, beforeOperations);
            if (!renameMap.TryGetValue(oldPath, out var translated)) continue;
            var match = right.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.RuleId, oldFinding.RuleId, StringComparison.Ordinal) &&
                string.Equals(candidate.Title, oldFinding.Title, StringComparison.Ordinal) &&
                string.Equals(PrimaryPath(candidate, afterOperations), translated, StringComparison.Ordinal));
            if (match is null) continue;
            pairs.Add((oldFinding, match, true));
            left.Remove(oldFinding.Fingerprint);
            right.Remove(match.Fingerprint);
        }

        var newItems = right.Values.Select(finding => FindingItem(finding, afterOperations, after.Run.Kind)).ToArray();
        var resolvedItems = left.Values.Select(finding => FindingItem(finding, beforeOperations, before.Run.Kind)).ToArray();
        var persistingItems = pairs.Select(pair => FindingItem(pair.After, afterOperations, after.Run.Kind)).ToArray();
        var changes = pairs.Where(pair => pair.Renamed ||
                            !string.Equals(pair.Before.Severity, pair.After.Severity, StringComparison.Ordinal) ||
                            !string.Equals(pair.Before.State, pair.After.State, StringComparison.Ordinal))
            .Select(pair => new ReviewRunPersistingFindingChange(
                pair.After.Fingerprint,
                pair.Before.Fingerprint,
                pair.After.Fingerprint,
                pair.Before.Severity,
                pair.After.Severity,
                pair.Before.State,
                pair.After.State,
                pair.Renamed)).ToArray();
        return (new FindingDelta(newItems, resolvedItems, persistingItems), changes);
    }

    private static FindingDeltaItem FindingItem(
        ReviewRunFindingRecord finding,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> operations,
        string kind)
    {
        var operation = operations.Values.FirstOrDefault(candidate => candidate.OperationId == finding.OperationId);
        return new FindingDeltaItem(finding.Fingerprint, operation?.UnitId ?? "unknown",
            operation?.Path ?? finding.Locations.FirstOrDefault()?.Path ?? "unknown", kind,
            finding.RuleId, finding.Severity, finding.Title);
    }

    private static string PrimaryPath(
        ReviewRunFindingRecord finding,
        IReadOnlyDictionary<string, ReviewRunOperationRecord> operations) =>
        finding.Locations.FirstOrDefault()?.Path ??
        operations.Values.FirstOrDefault(operation => operation.OperationId == finding.OperationId)?.Path ?? string.Empty;

    private static ReviewRunExecutionDelta ExecutionDelta(
        ReviewRunHistoryDetail before,
        ReviewRunHistoryDetail after)
    {
        var left = ExecutionSnapshot(before);
        var right = ExecutionSnapshot(after);
        return new ReviewRunExecutionDelta(left, right,
            right.FailedFiles - left.FailedFiles,
            right.SkippedFiles - left.SkippedFiles,
            right.DurationMs - left.DurationMs);
    }

    private static ReviewRunExecutionSnapshot ExecutionSnapshot(ReviewRunHistoryDetail detail) => new(
        detail.Attempt.Outcome,
        detail.Attempt.Complete,
        detail.Attempt.Attempt,
        detail.Attempt.CumulativeCounters.FailedFiles,
        detail.Attempt.CumulativeCounters.SkippedFiles,
        Math.Max(0, (long)(detail.Attempt.FinishedAt - detail.Attempt.StartedAt).TotalMilliseconds),
        detail.Run.Configuration.TokenCap,
        detail.Run.Configuration.CostCap,
        detail.Attempt.EstimateDeviation);

    private static ReviewRunEconomyDelta EconomyDelta(
        ReviewRunAttemptRecord before,
        ReviewRunAttemptRecord after)
    {
        var left = EconomySnapshot(before);
        var right = EconomySnapshot(after);
        return new ReviewRunEconomyDelta(left, right,
            Difference(left.InputTokens, right.InputTokens),
            Difference(left.OutputTokens, right.OutputTokens),
            Difference(left.CachedInputTokens, right.CachedInputTokens),
            Difference(left.ReasoningOutputTokens, right.ReasoningOutputTokens),
            right.DurationMs - left.DurationMs,
            left.Cost.HasValue && right.Cost.HasValue ? right.Cost.Value - left.Cost.Value : null);
    }

    private static ReviewRunEconomySnapshot EconomySnapshot(ReviewRunAttemptRecord attempt) => new(
        attempt.CumulativeSpend.Tokens.InputTokens,
        attempt.CumulativeSpend.Tokens.OutputTokens,
        attempt.CumulativeSpend.Tokens.CachedInputTokens,
        attempt.CumulativeSpend.Tokens.ReasoningOutputTokens,
        attempt.CumulativeSpend.Tokens.DurationMs,
        attempt.CumulativeSpend.Cost,
        attempt.CumulativeSpend.Currency,
        attempt.CumulativeSpend.PriceStatus);

    private static long? Difference(long? before, long? after) =>
        before.HasValue && after.HasValue ? after.Value - before.Value : null;

    private static Dictionary<string, ReviewRunOperationRecord> LatestOperations(
        ReviewRunHistoryDetail detail,
        IReadOnlyDictionary<string, string>? renameMap) =>
        detail.Operations.GroupBy(operation => Translate(operation.Path, renameMap), StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(operation => operation.Attempt)
                    .ThenByDescending(operation => operation.Ordinal).First(), StringComparer.Ordinal);

    private static ReviewRunTargetChange TargetChange(
        ReviewRunPlanTarget target,
        string? beforeHash,
        string? afterHash) => new(target.Id, target.Path, beforeHash, afterHash);

    private static string Translate(string path, IReadOnlyDictionary<string, string>? renameMap) =>
        renameMap is not null && renameMap.TryGetValue(path, out var translated) ? translated : path;

    private static async Task<(IReadOnlyDictionary<string, string> Map, string Status)> RenameMapAsync(
        string repositoryRoot,
        string? beforeCommit,
        string? afterCommit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(beforeCommit) || string.IsNullOrWhiteSpace(afterCommit))
            return (new Dictionary<string, string>(StringComparer.Ordinal), "unavailable-revisions");
        if (string.Equals(beforeCommit, afterCommit, StringComparison.Ordinal))
            return (new Dictionary<string, string>(StringComparer.Ordinal), "not-needed");
        try
        {
            var changes = await new GitMergeRangeChangeSetProvider().GetAsync(
                new ChangeSetQuery(repositoryRoot, beforeCommit, afterCommit), cancellationToken).ConfigureAwait(false);
            var map = changes.SelectMany(change => change.TouchedFiles)
                .Where(path => path.Kind == ChangeKind.Renamed && path.PreviousPath is not null && !path.ContentChanged)
                .ToDictionary(path => path.PreviousPath!, path => path.Path, StringComparer.Ordinal);
            return (map, "available");
        }
        catch (Exception exception) when (exception is ChangeReviewException or IOException or InvalidOperationException)
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal), "unavailable-error");
        }
    }
}
