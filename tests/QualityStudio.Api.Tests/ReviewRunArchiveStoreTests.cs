using AgentOrchestrator.CodeQuality;
using Json.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Archive_contract_fixtures_validate_against_all_v1_schemas()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.Manifest("review-schema");
        var operation = fixture.Operation(manifest.RunId, "operation-schema", 1, 1, "src/a.cs", "done", "policy", 90);
        var finding = new ReviewRunFindingRecord(
            ReviewRunArchiveSchemas.Finding, 1, operation.OperationId, 1,
            "sha256:" + new string('a', 64), "finding-a", "rule-a", "high", "Finding", [], "open");
        fixture.Store.CreateRun(manifest);
        fixture.Store.AppendOperation(manifest.CreatedAt, operation);
        fixture.Store.AppendFindings(manifest.CreatedAt, manifest.RunId, [finding]);
        var attempt = fixture.Store.CreateAttemptRecord(
            manifest, fixture.Status(manifest.RunId, "done", 1, 2, 0), 1, fixture.CreatedAt);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var repositoryRoot = FindRepositoryRoot();

        AssertSchema("run-record.v1.schema.json", manifest);
        AssertSchema("run-operation.v1.schema.json", operation);
        AssertSchema("run-finding.v1.schema.json", finding);
        AssertSchema("run-attempt.v1.schema.json", attempt);
        return;

        void AssertSchema<T>(string fileName, T value)
        {
            var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(repositoryRoot, "schemas", fileName)));
            var json = JsonSerializer.SerializeToElement(value, options);
            var result = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, result.ToString());
        }
    }

    [Fact]
    public void Archive_is_create_only_path_confined_and_keeps_capped_resume_attempts()
    {
        using var fixture = new ArchiveFixture();
        var manifest = fixture.Manifest("review-attempts");
        fixture.Store.CreateRun(manifest);
        Assert.Throws<IOException>(() => fixture.Store.CreateRun(manifest));

        fixture.Store.AppendOperation(manifest.CreatedAt,
            fixture.Operation(manifest.RunId, "operation-1", 1, 1, "src/a.cs", "done", "policy-a", 82));
        var capped = fixture.Status(manifest.RunId, "capped", attempt: 1, completed: 1, skipped: 1);
        fixture.Store.CreateAttempt(manifest.CreatedAt,
            fixture.Store.CreateAttemptRecord(manifest, capped, 1, fixture.CreatedAt));

        fixture.Store.AppendOperation(manifest.CreatedAt,
            fixture.Operation(manifest.RunId, "operation-2", 2, 2, "src/b.cs", "done", "policy-a", 88));
        var done = fixture.Status(manifest.RunId, "done", attempt: 2, completed: 2, skipped: 0);
        fixture.Store.CreateAttempt(manifest.CreatedAt,
            fixture.Store.CreateAttemptRecord(manifest, done, 2, fixture.CreatedAt.AddMinutes(2)));

        var first = fixture.Store.Get("default", manifest.RunId, 1);
        var latest = fixture.Store.Get("default", manifest.RunId);
        Assert.Equal("capped", first.Attempt!.Outcome);
        Assert.Equal(1, Assert.Single(first.Operations).Attempt);
        Assert.Equal("done", latest.Attempt!.Outcome);
        Assert.Equal(2, latest.Attempt.Attempt);
        Assert.Equal(2, latest.Operations.Count);
        Assert.Throws<IOException>(() => fixture.Store.CreateAttempt(manifest.CreatedAt, latest.Attempt));

        var invalid = manifest with { RunId = "../outside" };
        Assert.Throws<ArgumentException>(() => fixture.Store.CreateRun(invalid));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "outside")));

        if (!OperatingSystem.IsWindows())
        {
            var linkedRoot = Path.Combine(fixture.Root, "linked-repository");
            var outside = Path.Combine(fixture.Root, "outside-history");
            Directory.CreateDirectory(Path.Combine(linkedRoot, ".quality"));
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(linkedRoot, ".quality", "run-history"), outside);
            Assert.Throws<ArgumentException>(() => new ReviewRunArchiveStore(linkedRoot));
        }
    }

    [Fact]
    public void Appends_are_idempotent_and_history_is_cursor_paged_with_typed_corruption()
    {
        using var fixture = new ArchiveFixture();
        var first = fixture.Manifest("review-first");
        fixture.Store.CreateRun(first);
        var operation = fixture.Operation(first.RunId, "operation-1", 1, 1, "src/a.cs", "done", "policy", 90);
        Assert.True(fixture.Store.AppendOperation(first.CreatedAt, operation));
        Assert.False(fixture.Store.AppendOperation(first.CreatedAt, operation));
        fixture.Store.CreateAttempt(first.CreatedAt,
            fixture.Store.CreateAttemptRecord(first, fixture.Status(first.RunId, "done", 1, 2, 0), 1,
                fixture.CreatedAt));

        var second = fixture.Manifest("review-second", fixture.CreatedAt.AddMinutes(1));
        fixture.Store.CreateRun(second);
        fixture.Store.CreateAttempt(second.CreatedAt,
            fixture.Store.CreateAttemptRecord(second, fixture.Status(second.RunId, "done", 1, 2, 0), 1,
                second.CreatedAt));

        var page1 = fixture.Store.Query("default", limit: 1);
        Assert.Equal(second.RunId, Assert.Single(page1.Runs).RunId);
        Assert.NotNull(page1.NextCursor);
        var page2 = fixture.Store.Query("default", page1.NextCursor, limit: 1);
        Assert.Equal(first.RunId, Assert.Single(page2.Runs).RunId);

        var attemptPath = Path.Combine(fixture.Store.HistoryPath, "2026-08", second.RunId, "attempts", "0001.json");
        File.WriteAllText(attemptPath, "{");
        var corrupt = Assert.Single(fixture.Store.Query("default", kind: "security").Runs,
            item => item.RunId == second.RunId);
        Assert.Equal("history-corrupt", corrupt.Error!.Code);
        Assert.Equal("history-corrupt", fixture.Store.Get("default", second.RunId).Error!.Code);
    }

    [Fact]
    public void V0_migration_is_idempotent_and_preserves_unknown_quality_facts()
    {
        using var fixture = new ArchiveFixture();
        var recovery = new ReviewRunStore(fixture.Root);
        var runId = "review-legacy";
        var manifest = new ReviewRunManifest(
            runId,
            "default",
            new ReviewRunPlanNode("file-a", "a.cs", "src/a.cs"),
            "file",
            "code",
            null,
            "codex",
            fixture.CreatedAt,
            [new ReviewRunPlanTarget("file-a", "a.cs", "src/a.cs", "sha256:abc")],
            null);
        var status = fixture.Status(runId, "done", 1, 1, 0);
        recovery.Create(manifest, status);
        recovery.AppendProgress(new ReviewRunFileTransition(
            "src/a.cs", "done", fixture.CreatedAt, fixture.CreatedAt.AddMinutes(1), runId, null));
        recovery.WriteStatus(status);
        var stored = Assert.Single(recovery.LoadAll());

        fixture.Store.MigrateFromRunStore(stored);
        fixture.Store.MigrateFromRunStore(stored);

        var detail = fixture.Store.Get("default", runId);
        Assert.Equal("migrated-from-run-store-v0", detail.Run!.Provenance);
        Assert.Equal("unknown", Assert.Single(detail.Operations).VerdictType);
        Assert.Equal("migrated-from-run-store-v0", detail.Attempt!.Provenance);
    }

    [Fact]
    public void Diff_classifies_exact_reruns()
    {
        using var fixture = new ArchiveFixture();
        var before = fixture.Detail("before", "model-a", "done", "policy-a", "src/a.cs", 80,
            new TokenUsage(100, 20, 0, null, 1000));
        var after = fixture.Detail("after", "model-a", "done", "policy-a", "src/a.cs", 85,
            new TokenUsage(110, 20, 0, null, 900));

        var diff = ReviewRunDiffService.Compare(before, after);

        Assert.Equal(["exact"], diff.Comparability);
        Assert.Equal(5, Assert.Single(diff.Grades).ScoreChange);
        Assert.Equal(10, diff.Economy.InputTokensChange);
        Assert.Null(diff.Economy.ReasoningOutputTokensChange);
    }

    [Fact]
    public void Diff_correlates_real_rename_mappings_without_reporting_scope_churn()
    {
        using var fixture = new ArchiveFixture();
        var before = fixture.Detail("before", "model-a", "done", "policy-a", "src/old.cs", 80,
            new TokenUsage(100, 20, 0, 0, 1000));
        var after = fixture.Detail("after", "model-a", "done", "policy-a", "src/new.cs", 85,
            new TokenUsage(100, 20, 0, 0, 1000));

        var diff = ReviewRunDiffService.Compare(before, after, renameMap:
            new Dictionary<string, string>(StringComparer.Ordinal) { ["src/old.cs"] = "src/new.cs" });

        Assert.Equal(["exact"], diff.Comparability);
        Assert.Empty(diff.Scope.Added);
        Assert.Empty(diff.Scope.Removed);
        Assert.Equal(["src/new.cs"], diff.Scope.Persisting);
        Assert.Equal("src/new.cs", Assert.Single(diff.Grades).Path);
        Assert.True(diff.RenameCorrelationAvailable);
    }

    [Fact]
    public async Task Legacy_usage_groups_are_visible_without_inventing_run_facts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = new ArchiveFixture();
        await UsageLedger.AppendAsync(fixture.Root, new ReviewUsageEntry(
            "provider-legacy",
            fixture.CreatedAt,
            "model-a",
            "codex",
            new TokenUsage(10, null, 2, null, 50),
            "code",
            "file",
            "src/a.cs",
            "review-usage-only",
            2), cancellationToken);

        var entries = await UsageLedger.ReadAllAsync(fixture.Root, cancellationToken: cancellationToken);
        var legacy = ReviewRunArchiveStore.LegacyUsageOnlyRows("default", entries);
        var row = Assert.Single(fixture.Store.Query("default", supplementalRows: legacy).Runs);

        Assert.Equal("review-usage-only", row.RunId);
        Assert.Equal("legacy-usage-only", row.Outcome);
        Assert.Equal("src/a.cs", row.Path);
        Assert.Equal("model-a", row.Model);
        Assert.Null(row.CreatedAt);
        Assert.Null(row.FinishedAt);
        Assert.Null(row.Complete);
        Assert.Equal(10, row.Usage!.InputTokens);
        Assert.Null(row.Usage.OutputTokens);

        var active = fixture.Manifest("review-active");
        fixture.Store.CreateRun(active);
        var activeUsage = ReviewRunArchiveStore.LegacyUsageOnlyRows("default", [new ReviewUsageEntry(
            "provider-active", fixture.CreatedAt, "model-a", "codex", new TokenUsage(1, 1, 0, 0, 1),
            "code", "file", "src/a.cs", active.RunId, 2)]);
        Assert.DoesNotContain(fixture.Store.Query("default", supplementalRows: activeUsage).Runs,
            candidate => candidate.RunId == active.RunId);
    }

    [Fact]
    public void Diff_surfaces_policy_model_scope_completeness_finding_and_economy_changes()
    {
        using var fixture = new ArchiveFixture();
        var before = fixture.Detail("before", "model-a", "capped", "policy-a", "src/a.cs", 80,
            new TokenUsage(null, 20, null, null, 1000), findingState: "open", targetHash: "hash-a");
        var after = fixture.Detail("after", "model-b", "done", "policy-b", "src/b.cs", 70,
            new TokenUsage(120, 30, 5, 2, 1500), findingState: "accepted", targetHash: "hash-b",
            extraFinding: true);

        var diff = ReviewRunDiffService.Compare(before, after);

        Assert.Contains("scope-changed", diff.Comparability);
        Assert.Contains("policy-changed", diff.Comparability);
        Assert.Contains("model-changed", diff.Comparability);
        Assert.Contains("incomplete", diff.Comparability);
        Assert.Equal(["src/b.cs"], diff.Scope.Added);
        Assert.Equal(["src/a.cs"], diff.Scope.Removed);
        Assert.Single(diff.Findings.Resolved);
        Assert.Single(diff.Findings.New);
        Assert.Equal("accepted", Assert.Single(diff.Findings.Persisting).AfterState);
        Assert.Null(diff.Economy.InputTokensChange);
        Assert.Equal(500, diff.Execution.DurationMsChange);
    }

    private sealed class ArchiveFixture : IDisposable
    {
        public ArchiveFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "quality-studio-archive-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new ReviewRunArchiveStore(Root);
        }

        public string Root { get; }
        public ReviewRunArchiveStore Store { get; }
        public DateTimeOffset CreatedAt { get; } = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

        public ReviewRunArchiveManifest Manifest(string runId, DateTimeOffset? createdAt = null) => new(
            ReviewRunArchiveSchemas.Run,
            1,
            runId,
            "default",
            createdAt ?? CreatedAt,
            new ReviewRunPlanNode("project-root", "Root", "."),
            "project",
            "code",
            [
                new ReviewRunPlanTarget("file-a", "a.cs", "src/a.cs", "hash-a"),
                new ReviewRunPlanTarget("file-b", "b.cs", "src/b.cs", "hash-b"),
            ],
            new ReviewRunArchiveConfiguration("model-a", "high", "codex", false, false),
            null,
            null,
            null,
            new ReviewRunSourceRevision(new string('a', 40), false),
            "native");

        public ReviewRunOperationRecord Operation(
            string runId,
            string operationId,
            int ordinal,
            int attempt,
            string path,
            string state,
            string policyHash,
            int grade) => new(
            ReviewRunArchiveSchemas.Operation,
            1,
            runId,
            operationId,
            ordinal,
            attempt,
            "unit-" + ordinal,
            path,
            "file",
            state,
            CreatedAt.AddMinutes(ordinal),
            CreatedAt.AddMinutes(ordinal + 1),
            "provider-" + ordinal,
            "subject-" + ordinal,
            policyHash,
            $".quality/reviews/{ordinal}.json",
            "grade",
            null,
            new ReviewRunArchiveGrade(grade, grade >= 80 ? "B" : "C", "Fixture"),
            CreatedAt.AddMinutes(ordinal + 1),
            CreatedAt.AddMinutes(ordinal),
            null);

        public ReviewRunStatus Status(string runId, string outcome, int attempt, int completed, int skipped) => new(
            runId,
            outcome,
            2,
            completed,
            0,
            completed,
            CreatedAt,
            CreatedAt,
            CreatedAt.AddMinutes(attempt + 1),
            [],
            completed,
            new TokenUsage(completed * 100, completed * 20, 0, null, completed * 1000),
            SkippedFiles: skipped,
            StopReason: outcome == "capped" ? "cap" : null,
            Attempt: attempt,
            AttemptStartedAt: CreatedAt.AddMinutes(attempt - 1));

        public ReviewRunHistoryDetail Detail(
            string runId,
            string model,
            string outcome,
            string policy,
            string path,
            int score,
            TokenUsage usage,
            string findingState = "open",
            string targetHash = "hash-a",
            bool extraFinding = false)
        {
            var manifest = Manifest(runId) with
            {
                Targets = [new ReviewRunPlanTarget("unit", Path.GetFileName(path), path, targetHash)],
                Configuration = new ReviewRunArchiveConfiguration(model, "high", "codex", false, false),
            };
            var operation = Operation(runId, runId + ":operation", 1, 1, path, "done", policy, score);
            var finding = new ReviewRunFindingRecord(
                ReviewRunArchiveSchemas.Finding, 1, operation.OperationId, 1,
                "sha256:" + new string('a', 64), "finding-a", "rule-a", "high", "A finding", [], findingState);
            var findings = new List<ReviewRunFindingRecord> { finding };
            if (extraFinding)
            {
                findings.Add(new ReviewRunFindingRecord(
                    ReviewRunArchiveSchemas.Finding, 1, operation.OperationId, 1,
                    "sha256:" + new string('b', 64), "finding-b", "rule-b", "medium", "Another finding", [], "open"));
            }
            if (outcome == "capped")
            {
                findings.Add(new ReviewRunFindingRecord(
                    ReviewRunArchiveSchemas.Finding, 1, operation.OperationId, 1,
                    "sha256:" + new string('d', 64), "finding-d", "rule-d", "low", "Resolved later", [], "open"));
            }
            var complete = outcome == "done";
            var attempt = new ReviewRunAttemptRecord(
                ReviewRunArchiveSchemas.Attempt, 1, runId, 1, outcome, complete,
                CreatedAt, CreatedAt.AddSeconds(1), 1, complete ? 1 : 0, 0, complete ? 0 : 1,
                complete ? "done" : "skipped", [], complete ? null : "cap", null, null, 1, usage,
                null, null, "unavailable", ["2026-08"], [operation.OperationId],
                new ReviewRunQualitySummary(score, score >= 80 ? "B" : "C", null, findings.Count,
                    findings.GroupBy(item => item.Severity).ToDictionary(group => group.Key, group => group.Count())),
                CreatedAt.AddSeconds(2));
            return new ReviewRunHistoryDetail(manifest, attempt, [operation], findings);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
