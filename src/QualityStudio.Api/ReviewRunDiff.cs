using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewRunTargetDelta(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Persisting,
    IReadOnlyList<string> ChangedSubjectHashes);

public sealed record ReviewRunGradeDelta(
    string Path,
    string Level,
    int? BeforeScore,
    int? AfterScore,
    int? ScoreChange,
    string? BeforeBand,
    string? AfterBand);

public sealed record ReviewRunVerdictDelta(
    string Path,
    string VerdictType,
    string? Before,
    string? After);

public sealed record ReviewRunPersistingFindingDelta(
    string Fingerprint,
    string BeforeSeverity,
    string AfterSeverity,
    string BeforeState,
    string AfterState);

public sealed record ReviewRunFindingDelta(
    IReadOnlyList<string> New,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<ReviewRunPersistingFindingDelta> Persisting);

public sealed record ReviewRunExecutionDelta(
    string BeforeOutcome,
    string AfterOutcome,
    bool BeforeComplete,
    bool AfterComplete,
    int FailedFilesChange,
    int SkippedFilesChange,
    long DurationMsChange,
    decimal? CostChange);

public sealed record ReviewRunEconomyDelta(
    TokenUsage Before,
    TokenUsage After,
    long? InputTokensChange,
    long? OutputTokensChange,
    long? CachedInputTokensChange,
    long? ReasoningOutputTokensChange,
    long DurationMsChange,
    decimal? CostChange,
    string? Currency);

public sealed record ReviewRunDiff(
    string BeforeRunId,
    int BeforeAttempt,
    string AfterRunId,
    int AfterAttempt,
    IReadOnlyList<string> Comparability,
    ReviewRunTargetDelta Scope,
    ReviewRunExecutionDelta Execution,
    IReadOnlyList<ReviewRunGradeDelta> Grades,
    IReadOnlyList<ReviewRunVerdictDelta> Verdicts,
    ReviewRunFindingDelta Findings,
    ReviewRunEconomyDelta Economy,
    bool RenameCorrelationAvailable);

public static class ReviewRunDiffService
{
    public static ReviewRunDiff Compare(
        ReviewRunHistoryDetail before,
        ReviewRunHistoryDetail after,
        bool allowScopeChange = false,
        IReadOnlyDictionary<string, string>? renameMap = null)
    {
        var beforeRun = RequireValid(before);
        var afterRun = RequireValid(after);
        var beforeAttempt = before.Attempt!;
        var afterAttempt = after.Attempt!;
        if (!string.Equals(beforeRun.RepositoryId, afterRun.RepositoryId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Review runs from different repositories cannot be compared.");
        if (!string.Equals(beforeRun.Kind, afterRun.Kind, StringComparison.Ordinal))
            throw new ArgumentException("Review runs with different review kinds cannot be compared.");
        var rootChanged = !string.Equals(
            Translate(beforeRun.Subject.Path, renameMap), afterRun.Subject.Path, StringComparison.Ordinal);
        if (rootChanged && !allowScopeChange)
            throw new ArgumentException("Review runs have different root units. Set allowScopeChange to compare them explicitly.");

        var beforeTargets = beforeRun.Targets
            .GroupBy(target => Translate(target.Path, renameMap), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var afterTargets = afterRun.Targets.ToDictionary(target => target.Path, StringComparer.Ordinal);
        var beforePaths = beforeTargets.Keys.ToHashSet(StringComparer.Ordinal);
        var afterPaths = afterTargets.Keys.ToHashSet(StringComparer.Ordinal);
        var added = afterPaths.Except(beforePaths, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var removed = beforePaths.Except(afterPaths, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var persisting = beforePaths.Intersect(afterPaths, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changedHashes = persisting.Where(path =>
                !string.Equals(beforeTargets[path].SubjectHash, afterTargets[path].SubjectHash, StringComparison.Ordinal))
            .ToArray();

        var comparability = new List<string>();
        if (rootChanged || added.Length > 0 || removed.Length > 0) comparability.Add("scope-changed");
        if (PolicyHashes(before).SetEquals(PolicyHashes(after)) is false) comparability.Add("policy-changed");
        if (!string.Equals(beforeRun.Configuration.Model, afterRun.Configuration.Model, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(beforeRun.Configuration.ThinkingLevel, afterRun.Configuration.ThinkingLevel, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(beforeRun.Configuration.CliType, afterRun.Configuration.CliType, StringComparison.OrdinalIgnoreCase))
            comparability.Add("model-changed");
        if (!beforeAttempt.Complete || !afterAttempt.Complete) comparability.Add("incomplete");
        if (comparability.Count == 0) comparability.Add("exact");

        var beforeOperations = LatestOperations(before.Operations, renameMap);
        var afterOperations = LatestOperations(after.Operations, null);
        var operationKeys = beforeOperations.Keys.Union(afterOperations.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var grades = new List<ReviewRunGradeDelta>();
        var verdicts = new List<ReviewRunVerdictDelta>();
        foreach (var key in operationKeys)
        {
            beforeOperations.TryGetValue(key, out var beforeOperation);
            afterOperations.TryGetValue(key, out var afterOperation);
            if (beforeOperation?.Grade is not null || afterOperation?.Grade is not null)
            {
                grades.Add(new ReviewRunGradeDelta(
                    afterOperation?.Path ?? beforeOperation!.Path,
                    afterOperation?.Level ?? beforeOperation!.Level,
                    beforeOperation?.Grade?.Score,
                    afterOperation?.Grade?.Score,
                    beforeOperation?.Grade is not null && afterOperation?.Grade is not null
                        ? afterOperation.Grade.Score - beforeOperation.Grade.Score
                        : null,
                    beforeOperation?.Grade?.Band,
                    afterOperation?.Grade?.Band));
            }
            if (beforeOperation?.Verdict is not null || afterOperation?.Verdict is not null)
            {
                verdicts.Add(new ReviewRunVerdictDelta(
                    afterOperation?.Path ?? beforeOperation!.Path,
                    afterOperation?.VerdictType ?? beforeOperation!.VerdictType,
                    beforeOperation?.Verdict,
                    afterOperation?.Verdict));
            }
        }

        var beforeFindings = before.Findings.GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var afterFindings = after.Findings.GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var newFindings = afterFindings.Keys.Except(beforeFindings.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var resolvedFindings = beforeFindings.Keys.Except(afterFindings.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var persistentFindings = beforeFindings.Keys.Intersect(afterFindings.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(fingerprint => new ReviewRunPersistingFindingDelta(
                fingerprint,
                beforeFindings[fingerprint].Severity,
                afterFindings[fingerprint].Severity,
                beforeFindings[fingerprint].StateAtObservation,
                afterFindings[fingerprint].StateAtObservation))
            .ToArray();

        decimal? costChange = beforeAttempt.CostSpent.HasValue && afterAttempt.CostSpent.HasValue &&
                         string.Equals(beforeAttempt.Currency, afterAttempt.Currency, StringComparison.Ordinal)
            ? afterAttempt.CostSpent.Value - beforeAttempt.CostSpent.Value
            : null;
        var durationChange = afterAttempt.Usage.DurationMs - beforeAttempt.Usage.DurationMs;
        return new ReviewRunDiff(
            beforeRun.RunId,
            beforeAttempt.Attempt,
            afterRun.RunId,
            afterAttempt.Attempt,
            comparability,
            new ReviewRunTargetDelta(added, removed, persisting, changedHashes),
            new ReviewRunExecutionDelta(
                beforeAttempt.Outcome,
                afterAttempt.Outcome,
                beforeAttempt.Complete,
                afterAttempt.Complete,
                afterAttempt.FailedFiles - beforeAttempt.FailedFiles,
                afterAttempt.SkippedFiles - beforeAttempt.SkippedFiles,
                durationChange,
                costChange),
            grades,
            verdicts,
            new ReviewRunFindingDelta(newFindings, resolvedFindings, persistentFindings),
            new ReviewRunEconomyDelta(
                beforeAttempt.Usage,
                afterAttempt.Usage,
                Difference(beforeAttempt.Usage.InputTokens, afterAttempt.Usage.InputTokens),
                Difference(beforeAttempt.Usage.OutputTokens, afterAttempt.Usage.OutputTokens),
                Difference(beforeAttempt.Usage.CachedInputTokens, afterAttempt.Usage.CachedInputTokens),
                Difference(beforeAttempt.Usage.ReasoningOutputTokens, afterAttempt.Usage.ReasoningOutputTokens),
                durationChange,
                costChange,
                string.Equals(beforeAttempt.Currency, afterAttempt.Currency, StringComparison.Ordinal)
                    ? afterAttempt.Currency
                    : null),
            renameMap is not null);
    }

    private static ReviewRunArchiveManifest RequireValid(ReviewRunHistoryDetail detail)
    {
        if (detail.Error is not null)
            throw new InvalidDataException($"{detail.Error.Code}: {detail.Error.Detail}");
        return detail.Run ?? throw new InvalidDataException("Archived review run manifest is missing.");
    }

    private static HashSet<string> PolicyHashes(ReviewRunHistoryDetail detail) => detail.Operations
        .Select(operation => operation.ReviewInputsHash)
        .Where(hash => !string.IsNullOrWhiteSpace(hash))
        .Cast<string>()
        .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, ReviewRunOperationRecord> LatestOperations(
        IReadOnlyList<ReviewRunOperationRecord> operations,
        IReadOnlyDictionary<string, string>? renameMap) => operations
        .GroupBy(operation => $"{operation.Level}\0{Translate(operation.Path, renameMap)}", StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.OrderBy(operation => operation.Attempt).Last(),
            StringComparer.Ordinal);

    private static long? Difference(long? before, long? after) =>
        before.HasValue && after.HasValue ? after.Value - before.Value : null;

    private static string Translate(string path, IReadOnlyDictionary<string, string>? renameMap) =>
        renameMap is not null && renameMap.TryGetValue(path, out var translated) ? translated : path;
}
