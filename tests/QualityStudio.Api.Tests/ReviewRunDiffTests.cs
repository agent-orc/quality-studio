using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunDiffTests
{
    private static readonly DateTimeOffset Time = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Diff_golden_covers_scope_policy_model_completeness_quality_findings_and_unknown_economy()
    {
        var before = Detail(
            "before", "gpt-5", "done", true,
            [Target("a", "src/A.cs", "hash-a"), Target("b", "src/B.cs", "hash-b")],
            [Operation("before", "op-a", "a", "src/A.cs", 80, "policy-1"),
             Operation("before", "op-b", "b", "src/B.cs", 90, "policy-1", "pass")],
            [Finding("before", "op-a", "fingerprint-old", "src/A.cs", "high", "open"),
             Finding("before", "op-b", "fingerprint-persist", "src/B.cs", "low", "open")],
            new TokenUsage(100, 20, 10, null, 1000), 1.00m);
        var after = Detail(
            "after", "gpt-5-new", "capped", false,
            [Target("a", "src/A.cs", "hash-a-changed"), Target("c", "src/C.cs", "hash-c")],
            [Operation("after", "op-a2", "a", "src/A.cs", 70, "policy-2"),
             Operation("after", "op-c", "c", "src/C.cs", 85, "policy-1", "block")],
            [Finding("after", "op-c", "fingerprint-persist", "src/C.cs", "high", "accepted"),
             Finding("after", "op-a2", "fingerprint-new", "src/A.cs", "medium", "open")],
            new TokenUsage(null, 30, 12, null, 1500), null);

        var diff = await ReviewRunDiffService.CompareAsync(Path.GetTempPath(), before, after,
            cancellationToken: TestContext.Current.CancellationToken);

        var golden = JsonSerializer.Serialize(new
        {
            diff.Comparability.Labels,
            Added = diff.Scope.Added.Select(target => target.Path),
            Removed = diff.Scope.Removed.Select(target => target.Path),
            Changed = diff.Scope.ChangedHashes.Select(target => target.Path),
            InputChanges = diff.Inputs.Select(input => input.Path),
            diff.Execution.After.Outcome,
            GradeChanges = diff.Grades.Select(grade => new { grade.UnitPath, grade.ScoreChange, grade.Regression }),
            VerdictChanges = diff.Verdicts.Select(verdict => new
                { UnitPath = verdict.Path, verdict.Before, verdict.After }),
            FindingCounts = new
            {
                New = diff.Findings.New.Count,
                Resolved = diff.Findings.Resolved.Count,
                Persisting = diff.Findings.Persisting.Count,
            },
            FindingChanges = diff.FindingChanges.Select(change => new
                { change.Identity, change.BeforeSeverity, change.AfterSeverity, change.BeforeState, change.AfterState }),
            diff.Economy.InputTokensChange,
            diff.Economy.OutputTokensChange,
            diff.Economy.CostChange,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(
            "{\"labels\":[\"scope-changed\",\"policy-changed\",\"model-changed\",\"incomplete\"]," +
            "\"added\":[\"src/C.cs\"],\"removed\":[\"src/B.cs\"],\"changed\":[\"src/A.cs\"]," +
            "\"inputChanges\":[\"src/A.cs\",\"src/B.cs\",\"src/C.cs\"],\"outcome\":\"capped\"," +
            "\"gradeChanges\":[{\"unitPath\":\"src/A.cs\",\"scoreChange\":-10,\"regression\":true}," +
            "{\"unitPath\":\"src/B.cs\",\"scoreChange\":null,\"regression\":false}," +
            "{\"unitPath\":\"src/C.cs\",\"scoreChange\":null,\"regression\":false}]," +
            "\"verdictChanges\":[{\"unitPath\":\"src/B.cs\",\"before\":\"pass\",\"after\":null}," +
            "{\"unitPath\":\"src/C.cs\",\"before\":null,\"after\":\"block\"}]," +
            "\"findingCounts\":{\"new\":1,\"resolved\":1,\"persisting\":1}," +
            "\"findingChanges\":[{\"identity\":\"fingerprint-persist\",\"beforeSeverity\":\"low\"," +
            "\"afterSeverity\":\"high\",\"beforeState\":\"open\",\"afterState\":\"accepted\"}]," +
            "\"inputTokensChange\":null,\"outputTokensChange\":10,\"costChange\":null}", golden);
    }

    [Fact]
    public async Task Exact_rerun_is_exact_and_root_change_requires_explicit_override()
    {
        var detail = Detail(
            "same", "gpt-5", "done", true,
            [Target("a", "src/A.cs", "hash-a")],
            [Operation("same", "op-a", "a", "src/A.cs", 90, "policy-1")],
            [],
            new TokenUsage(10, 2, null, null, 100), null);

        var exact = await ReviewRunDiffService.CompareAsync(Path.GetTempPath(), detail, detail,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["exact"], exact.Comparability.Labels);
        Assert.Equal(0, exact.Economy.InputTokensChange);
        Assert.Null(exact.Economy.CachedInputTokensChange);

        var changedRoot = detail with
        {
            Run = detail.Run with { RunId = "other", Subject = new ReviewRunPlanNode("other", "Other", "other") },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => ReviewRunDiffService.CompareAsync(
            Path.GetTempPath(), detail, changedRoot, cancellationToken: TestContext.Current.CancellationToken));
        var allowed = await ReviewRunDiffService.CompareAsync(Path.GetTempPath(), detail, changedRoot,
            allowScopeChange: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("scope-changed", allowed.Comparability.Labels);

        var changedThinking = detail with
        {
            Run = detail.Run with
            {
                RunId = "changed-thinking",
                Configuration = detail.Run.Configuration with { ThinkingLevel = "low" },
            },
        };
        var thinkingDiff = await ReviewRunDiffService.CompareAsync(Path.GetTempPath(), detail, changedThinking,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("model-changed", thinkingDiff.Comparability.Labels);
    }

    private static ReviewRunHistoryDetail Detail(
        string id,
        string model,
        string outcome,
        bool complete,
        IReadOnlyList<ReviewRunPlanTarget> targets,
        IReadOnlyList<ReviewRunOperationRecord> operations,
        IReadOnlyList<ReviewRunFindingRecord> findings,
        TokenUsage usage,
        decimal? cost)
    {
        var run = new ReviewRunArchiveRecord
        {
            RunId = id,
            RepositoryId = "default",
            CreatedAt = Time,
            Subject = new ReviewRunPlanNode("root", "Root", "."),
            Level = "project",
            Kind = "security",
            Targets = targets,
            Configuration = new ReviewRunArchiveConfiguration(model, "high", "codex", false, 1000, null, null),
            SourceRevision = new ReviewRunSourceRevision(null, null),
        };
        var counters = new ReviewRunAttemptCounters(targets.Count, complete ? targets.Count : 1, 0,
            complete ? 0 : 1, operations.Count);
        var spend = new ReviewRunAttemptSpend(usage, cost, cost.HasValue ? "USD" : null,
            cost.HasValue ? "priced" : "usageUnavailable");
        var attempt = new ReviewRunAttemptRecord
        {
            RunId = id,
            Attempt = 1,
            Outcome = outcome,
            Complete = complete,
            StartedAt = Time,
            FinishedAt = Time.AddSeconds(1),
            ArchivedAt = Time.AddSeconds(2),
            Counters = counters,
            Spend = spend,
            CumulativeCounters = counters,
            CumulativeSpend = spend,
            ErrorCodes = [],
            LedgerMonths = ["2026-08"],
            OperationIds = operations.Select(operation => operation.OperationId).ToArray(),
            Quality = new ReviewRunAttemptQualitySummary(null, null, null, findings.Count, null),
        };
        return new ReviewRunHistoryDetail(run, attempt, operations, findings);
    }

    private static ReviewRunPlanTarget Target(string id, string path, string hash) => new(id, id, path, hash);

    private static ReviewRunOperationRecord Operation(
        string runId,
        string operationId,
        string unitId,
        string path,
        int grade,
        string inputHash,
        string? verdict = null) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Ordinal = 1,
        Attempt = 1,
        UnitId = unitId,
        Path = path,
        Level = "file",
        State = "done",
        StartedAt = Time,
        FinishedAt = Time.AddMilliseconds(100),
        ReviewInputsHash = inputHash,
        Grade = new ReviewRunArchivedGrade(grade, grade >= 90 ? "A" : grade >= 80 ? "B" : "C"),
        Verdict = verdict is null ? null : new ReviewRunTypedVerdict("security", verdict),
    };

    private static ReviewRunFindingRecord Finding(
        string runId,
        string operationId,
        string fingerprint,
        string path,
        string severity,
        string state) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Fingerprint = fingerprint,
        FindingId = fingerprint,
        RuleId = "rule",
        Severity = severity,
        Title = "Finding",
        Locations = [new FindingLocation(path)],
        State = state,
    };
}
