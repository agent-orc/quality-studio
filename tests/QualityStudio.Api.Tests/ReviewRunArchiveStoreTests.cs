using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Fixtures_validate_against_all_four_run_archive_schemas()
    {
        var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        AssertValid("run-record.v1.schema.json", CreateRunRecord("review-fixture", createdAt));
        AssertValid("run-operation.v1.schema.json", CreateOperation("review-fixture", attempt: 1, ordinal: 1));
        AssertValid("run-finding.v1.schema.json", CreateFinding("review-fixture", "op-review-fixture-0001-0001"));
        AssertValid("run-attempt.v1.schema.json", CreateAttempt("review-fixture", attempt: 1, createdAt.AddMinutes(2)));
    }

    [Fact]
    public void Store_rejects_overwriting_an_existing_run_record_or_attempt()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-store-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
            var record = CreateRunRecord("review-overwrite", createdAt);
            store.CreateRun(record);

            Assert.Throws<IOException>(() => store.CreateRun(record));

            var attempt = CreateAttempt("review-overwrite", attempt: 1, createdAt.AddMinutes(2));
            store.CreateAttempt(attempt);
            Assert.Throws<IOException>(() => store.CreateAttempt(attempt));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_run_remain_readable_with_their_operations_and_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-store-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
            store.CreateRun(CreateRunRecord("review-resumed", createdAt));

            var firstOperation = CreateOperation("review-resumed", attempt: 1, ordinal: 1);
            store.AppendOperation(firstOperation);
            store.AppendFinding(CreateFinding("review-resumed", firstOperation.OperationId));
            store.CreateAttempt(CreateAttempt("review-resumed", attempt: 1, createdAt.AddMinutes(2), outcome: "capped"));

            var secondOperation = CreateOperation("review-resumed", attempt: 2, ordinal: 2);
            store.AppendOperation(secondOperation);
            store.CreateAttempt(CreateAttempt("review-resumed", attempt: 2, createdAt.AddMinutes(5), outcome: "done"));

            var loaded = store.LoadRun("review-resumed");

            Assert.NotNull(loaded);
            Assert.Equal("review-resumed", loaded!.Record.RunId);
            Assert.Equal([1, 2], loaded.Attempts.Select(attempt => attempt.Attempt));
            Assert.Equal("capped", loaded.Attempts[0].Outcome);
            Assert.Equal("done", loaded.Attempts[1].Outcome);
            Assert.Equal(2, loaded.Operations.Count);
            Assert.Single(loaded.Findings);
            Assert.True(File.Exists(Path.Combine(store.ArchiveRoot, "2026-08", "review-resumed", "attempts", "0001.json")));
            Assert.True(File.Exists(Path.Combine(store.ArchiveRoot, "2026-08", "review-resumed", "attempts", "0002.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Loading_an_unknown_run_returns_null_and_appending_before_creation_fails()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-store-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Null(store.LoadRun("review-missing"));
            Assert.Throws<InvalidOperationException>(
                () => store.AppendOperation(CreateOperation("review-missing", attempt: 1, ordinal: 1)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/id")]
    public void Run_ids_with_path_separators_are_rejected(string runId)
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-store-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRunRecord(runId, DateTimeOffset.UtcNow)));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories),
                entry => !entry.StartsWith(store.ArchiveRoot, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web);

    private static void AssertValid(string schemaFileName, object document)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", schemaFileName)));
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(document, document.GetType(), DocumentJsonOptions));
        var result = schema.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static RunArchiveRecord CreateRunRecord(string runId, DateTimeOffset createdAt) => new(
        RunArchiveRecord.SchemaId,
        1,
        runId,
        "default",
        new ReviewRunPlanNode("file-sample", "Sample.cs", "Sample.cs"),
        "file",
        "code",
        "claude-sonnet-5",
        "high",
        "codex",
        createdAt,
        [new ReviewRunPlanTarget("file-sample", "Sample.cs", "Sample.cs",
            "sha256:" + new string('a', 64))],
        Force: false,
        TokenCap: 10_000,
        CostCap: null,
        Estimate: new ReviewRunEstimate(1, 1, 400, 800, 200, 0.01m, "USD", "priced", 3, "history-median"),
        Recommendation: new ReviewModelRecommendation(
            "policy-1", "claude-sonnet-5", "high", "standard", 80, "none", "Fixture reason.", "policy"),
        RouteOverride: false,
        SourceRevision: "3e0b6559faa700183a16e2ffda1a522fd75c9aa6",
        SourceDirty: false);

    private static RunOperationRecord CreateOperation(string runId, int attempt, int ordinal) => new(
        RunOperationRecord.SchemaId,
        1,
        $"op-{runId}-{attempt:0000}-{ordinal:0000}",
        runId,
        attempt,
        ordinal,
        "file-sample",
        "Sample.cs",
        "file",
        "done",
        StartedAt: DateTimeOffset.Parse("2026-08-11T09:00:10Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-11T09:00:20Z"),
        ProviderRunId: "provider-run-1",
        SubjectHash: "sha256:" + new string('a', 64),
        ReviewedHash: "sha256:" + new string('b', 64),
        SidecarPath: ".quality/reviews/files/sample.review-meta.code.json",
        Grade: new QualityRunGrade(90, "A", "Fixture grade."),
        SecurityVerdict: null,
        Error: null);

    private static RunFindingRecord CreateFinding(string runId, string operationId) => new(
        RunFindingRecord.SchemaId,
        1,
        runId,
        operationId,
        "sha256:" + new string('c', 64),
        "finding-1",
        "quality.rule.correctness",
        "medium",
        "Fixture finding",
        [new QualityFindingLocation("Sample.cs", 1, 1, 1, 8)],
        "open",
        DateTimeOffset.Parse("2026-08-11T09:00:20Z"));

    private static RunAttemptRecord CreateAttempt(
        string runId, int attempt, DateTimeOffset finishedAt, string outcome = "done") => new(
        RunAttemptRecord.SchemaId,
        1,
        runId,
        attempt,
        outcome,
        outcome == "done" ? "complete" : "partial",
        StartedAt: finishedAt.AddMinutes(-2),
        FinishedAt: finishedAt,
        TotalFiles: 1,
        CompletedFiles: outcome == "done" ? 1 : 0,
        FailedFiles: 0,
        SkippedFiles: outcome == "done" ? 0 : 1,
        AttemptUsage: new TokenUsage(800, 200, 0, 0, 1200),
        CumulativeUsage: new TokenUsage(800, 200, 0, 0, 1200),
        AttemptCostSpent: 0.01m,
        CumulativeCostSpent: 0.01m,
        Currency: "USD",
        PriceStatus: "priced",
        Errors: [],
        StopReason: outcome == "capped" ? "Token cap of 10,000 reached." : null,
        TokenCap: 10_000,
        CostCap: null,
        EstimateDeviation: new RunAttemptEstimateDeviation(0m, 0m, 0m, "Fixture deviation."),
        UsageLedgerMonths: ["2026-08"],
        QualitySummary: new RunAttemptQualitySummary(90, "A", null, 1, "medium"));
}
