using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Run_record_operation_finding_and_attempt_fixtures_validate_against_their_schemas()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var createdAt = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
            store.CreateRun(SampleRecord("review-fixture", createdAt));
            store.AppendOperation(SampleOperation("review-fixture", "op-1", ordinal: 0));
            store.AppendFinding(SampleFinding("review-fixture", "op-1"));
            store.WriteAttempt(SampleAttempt("review-fixture", attempt: 1));

            AssertValid("run-record.v1.schema.json", ReadJson(Path.Combine(store.ArchivePath, "2026-08", "review-fixture", "run.json")));
            AssertValid("run-operation.v1.schema.json", ReadFirstJsonLine(Path.Combine(store.ArchivePath, "2026-08", "review-fixture", "operations.jsonl")));
            AssertValid("run-finding.v1.schema.json", ReadFirstJsonLine(Path.Combine(store.ArchivePath, "2026-08", "review-fixture", "findings.jsonl")));
            AssertValid("run-attempt.v1.schema.json", ReadJson(Path.Combine(store.ArchivePath, "2026-08", "review-fixture", "attempts", "0001.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CreateRun_rejects_a_second_write_for_the_same_run_id()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var createdAt = DateTimeOffset.UtcNow;
            store.CreateRun(SampleRecord("review-dupe", createdAt));

            Assert.Throws<IOException>(() => store.CreateRun(SampleRecord("review-dupe", createdAt)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WriteAttempt_rejects_overwriting_an_existing_ordinal()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            store.CreateRun(SampleRecord("review-attempt-dupe", DateTimeOffset.UtcNow));
            store.WriteAttempt(SampleAttempt("review-attempt-dupe", attempt: 1));

            Assert.Throws<IOException>(() => store.WriteAttempt(SampleAttempt("review-attempt-dupe", attempt: 1)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void A_capped_run_that_resumes_twice_keeps_both_stopped_attempts_readable()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var runId = "review-capped-resume";
            store.CreateRun(SampleRecord(runId, DateTimeOffset.UtcNow));

            var firstOrdinal = store.NextAttemptOrdinal(runId);
            Assert.Equal(1, firstOrdinal);
            store.WriteAttempt(SampleAttempt(runId, firstOrdinal, outcome: "capped"));

            var secondOrdinal = store.NextAttemptOrdinal(runId);
            Assert.Equal(2, secondOrdinal);
            store.WriteAttempt(SampleAttempt(runId, secondOrdinal, outcome: "done"));

            var archived = store.LoadRun(runId);
            Assert.NotNull(archived);
            Assert.Equal(2, archived!.Attempts.Count);
            Assert.Equal("capped", archived.Attempts[0].Outcome);
            Assert.Equal(1, archived.Attempts[0].Attempt);
            Assert.Equal("done", archived.Attempts[1].Outcome);
            Assert.Equal(2, archived.Attempts[1].Attempt);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadRun_reads_back_operations_and_findings_appended_after_creation()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var runId = "review-load";
            store.CreateRun(SampleRecord(runId, DateTimeOffset.UtcNow));
            store.AppendOperation(SampleOperation(runId, "op-1", ordinal: 0));
            store.AppendOperation(SampleOperation(runId, "op-2", ordinal: 1));
            store.AppendFinding(SampleFinding(runId, "op-1"));

            var archived = store.LoadRun(runId);

            Assert.NotNull(archived);
            Assert.Equal(runId, archived!.Record.RunId);
            Assert.Equal(2, archived.Operations.Count);
            Assert.Equal("op-1", archived.Operations[0].OperationId);
            Assert.Single(archived.Findings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadRun_returns_null_for_an_unarchived_run()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Null(store.LoadRun("review-never-archived"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/id")]
    [InlineData("..")]
    [InlineData(".")]
    public void Archive_operations_reject_run_ids_that_are_not_a_single_path_segment(string runId)
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRecord(runId, DateTimeOffset.UtcNow)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AppendOperation_requires_the_run_to_be_created_first()
    {
        var root = TemporaryRepository();
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<InvalidOperationException>(() =>
                store.AppendOperation(SampleOperation("review-missing", "op-1", ordinal: 0)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AssertValid(string schemaFileName, JsonElement instance)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", schemaFileName)));
        var result = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static JsonElement ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    private static JsonElement ReadFirstJsonLine(string path)
    {
        var line = File.ReadLines(path).First(candidate => !string.IsNullOrWhiteSpace(candidate));
        return JsonDocument.Parse(line).RootElement;
    }

    private static ArchivedRunRecord SampleRecord(string runId, DateTimeOffset createdAt) => new(
        Schema: "placeholder",
        SchemaVersion: 1,
        RunId: runId,
        RepositoryId: "default",
        CreatedAt: createdAt,
        Node: new ArchivedRunNode("root", "Repository", "."),
        Level: "file",
        Kind: "code",
        Targets: [new ArchivedRunTarget("unit-1", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64))],
        Model: "claude-sonnet-5",
        ThinkingLevel: "high",
        CliType: "test-agent",
        Force: false,
        TokenCap: 10000,
        CostCap: null,
        Estimate: new ArchivedRunEstimate(1, 1, 100, 20, 0.01m, "USD", 3, "history-average"),
        SourceRevision: "abc123",
        SourceDirty: false);

    private static ArchivedRunOperation SampleOperation(string runId, string operationId, int ordinal) => new(
        Schema: "placeholder",
        SchemaVersion: 1,
        RunId: runId,
        OperationId: operationId,
        Ordinal: ordinal,
        Attempt: 1,
        UnitId: "unit-1",
        Path: "Sample.cs",
        Level: "file",
        State: "done",
        StartedAt: DateTimeOffset.UtcNow,
        FinishedAt: DateTimeOffset.UtcNow,
        ProviderRunId: "provider-run-1",
        SubjectHash: "sha256:" + new string('a', 64),
        ReviewedHash: "sha256:" + new string('b', 64),
        SidecarPath: "Sample.cs.review.json",
        SidecarSha256: "sha256:" + new string('c', 64),
        Grade: new ArchivedRunGrade(88, "B", "Minor findings only."),
        SecurityVerdict: null);

    private static ArchivedRunFinding SampleFinding(string runId, string operationId) => new(
        Schema: "placeholder",
        SchemaVersion: 1,
        RunId: runId,
        OperationId: operationId,
        FindingId: "finding-1",
        RuleId: "quality.rule.0",
        Severity: "medium",
        Title: "Sample finding",
        Fingerprint: "sha256:" + new string('d', 64),
        Locations: [new ArchivedRunFindingLocation("Sample.cs", 10, 1, 12, 1)],
        State: "open",
        ObservedAt: DateTimeOffset.UtcNow);

    private static ArchivedRunAttempt SampleAttempt(string runId, int attempt, string outcome = "done") => new(
        Schema: "placeholder",
        SchemaVersion: 1,
        RunId: runId,
        Attempt: attempt,
        Outcome: outcome,
        Completeness: outcome == "capped" ? "partial" : "complete",
        StartedAt: DateTimeOffset.UtcNow,
        FinishedAt: DateTimeOffset.UtcNow,
        Counters: new ArchivedAttemptCounters(1, 1, 0, 0),
        Usage: new ArchivedAttemptUsage(1, 100, 20, null, null, 500, 0.01m, "USD", "priced"),
        Errors: [],
        StopReason: outcome == "capped" ? "Token cap reached." : null,
        LedgerMonths: ["2026-08"],
        Summary: new ArchivedAttemptSummary("B", null, 1, "medium"));

    private static string TemporaryRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        var embeddedRoot = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "QualityStudioRepositoryRoot")?.Value;
        if (string.IsNullOrWhiteSpace(embeddedRoot) || !File.Exists(Path.Combine(embeddedRoot, "QualityStudio.slnx")))
            throw new DirectoryNotFoundException("Quality Studio repository root metadata was not embedded in the test assembly.");
        return embeddedRoot;
    }
}
