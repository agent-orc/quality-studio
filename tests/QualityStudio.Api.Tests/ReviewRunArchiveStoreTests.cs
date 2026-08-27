using System.Text.Json;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Run_operation_finding_and_attempt_fixtures_validate_against_their_v1_schemas()
    {
        var root = RepositoryRoot();
        AssertValid(Path.Combine(root, "schemas", "run-record.v1.schema.json"),
            ReviewRunArchiveJson.SerializeRun(SampleRun("schema-fixture")));
        AssertValid(Path.Combine(root, "schemas", "run-operation.v1.schema.json"),
            ReviewRunArchiveJson.SerializeOperationLine(SampleOperation()));
        AssertValid(Path.Combine(root, "schemas", "run-finding.v1.schema.json"),
            ReviewRunArchiveJson.SerializeFindingLine(SampleFinding()));
        AssertValid(Path.Combine(root, "schemas", "run-attempt.v1.schema.json"),
            ReviewRunArchiveJson.SerializeAttempt(SampleAttempt(1, "capped")));
    }

    [Fact]
    public void Create_run_rejects_a_second_write_for_the_same_run_id()
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            store.CreateRun(SampleRun("dup-run"));

            Assert.Throws<IOException>(() => store.CreateRun(SampleRun("dup-run")));
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    [Fact]
    public void Create_attempt_rejects_overwriting_an_existing_attempt_number()
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            store.CreateRun(SampleRun("dup-attempt"));
            store.CreateAttempt("dup-attempt", SampleAttempt(1, "capped"));

            Assert.Throws<IOException>(() => store.CreateAttempt("dup-attempt", SampleAttempt(1, "done")));
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            store.CreateRun(SampleRun("capped-then-resumed"));
            store.CreateAttempt("capped-then-resumed", SampleAttempt(1, "capped"));
            store.CreateAttempt("capped-then-resumed", SampleAttempt(2, "done"));

            var attempts = store.LoadAttempts("capped-then-resumed");

            Assert.Equal(2, attempts.Count);
            Assert.Equal(1, attempts[0].Attempt);
            Assert.Equal("capped", attempts[0].Outcome);
            Assert.Equal(2, attempts[1].Attempt);
            Assert.Equal("done", attempts[1].Outcome);
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    [Fact]
    public void Appended_operations_and_findings_survive_a_reload()
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            store.CreateRun(SampleRun("with-observations"));
            store.AppendOperation("with-observations", SampleOperation());
            store.AppendOperation("with-observations", SampleOperation() with { OperationId = "op-2", Ordinal = 1 });
            store.AppendFinding("with-observations", SampleFinding());

            var operations = store.LoadOperations("with-observations");
            var findings = store.LoadFindings("with-observations");

            Assert.Equal(2, operations.Count);
            Assert.Equal(["op-1", "op-2"], operations.Select(operation => operation.OperationId));
            Assert.Single(findings);
            Assert.Equal("op-1", findings[0].OperationId);
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    [Fact]
    public void Loading_or_appending_to_an_unknown_run_throws()
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);

            Assert.Throws<InvalidOperationException>(() => store.LoadRun("missing-run"));
            Assert.Throws<InvalidOperationException>(() => store.AppendOperation("missing-run", SampleOperation()));
            Assert.False(store.RunExists("missing-run"));
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/run")]
    public void Run_ids_containing_path_separators_are_rejected(string runId)
    {
        var repositoryRoot = CreateTempRepository();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(runId)));
        }
        finally
        {
            Delete(repositoryRoot);
        }
    }

    private static void AssertValid(string schemaPath, string json)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static RunArchiveRecord SampleRun(string runId) => new(
        ReviewRunArchiveJson.RunRecordSchemaId,
        1,
        runId,
        "default",
        new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
        "code",
        "file",
        "unit-file",
        "Sample.cs",
        [new RunArchiveTarget("unit-file", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64))],
        "claude-sonnet-5",
        "high",
        "codex",
        false,
        null,
        null,
        new RunArchiveEstimate(1, 1, 100, 25, 0.01m, "USD", 3, "history-average"),
        "3e0b6559faa700183a16e2ffda1a522fd75c9aa6",
        false);

    private static RunArchiveOperation SampleOperation() => new(
        ReviewRunArchiveJson.OperationSchemaId,
        1,
        "op-1",
        0,
        1,
        "unit-file",
        "file",
        "Sample.cs",
        "done",
        new DateTimeOffset(2026, 8, 11, 8, 0, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 11, 8, 0, 5, TimeSpan.Zero),
        "provider-run-1",
        "sha256:" + new string('b', 64),
        ".quality/reviews/root.review-meta.code.json",
        "sha256:" + new string('c', 64),
        new RunArchiveGrade(85, "B"),
        null,
        new DateTimeOffset(2026, 8, 11, 8, 0, 5, TimeSpan.Zero));

    private static RunArchiveFinding SampleFinding() => new(
        ReviewRunArchiveJson.FindingSchemaId,
        1,
        "op-1",
        "sha256:" + new string('d', 64),
        "finding-1",
        "quality.rule.correctness",
        "medium",
        "Sample finding",
        [new RunArchiveFindingLocation("Sample.cs", 10, 1, 10, 20)],
        "open",
        new DateTimeOffset(2026, 8, 11, 8, 0, 5, TimeSpan.Zero));

    private static RunArchiveAttempt SampleAttempt(int attempt, string outcome) => new(
        ReviewRunArchiveJson.AttemptSchemaId,
        1,
        attempt,
        outcome,
        outcome == "capped" ? "partial" : "complete",
        new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 11, 8, 5, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 11, 8, 5, 1, TimeSpan.Zero),
        new RunArchiveCounts(1, 0, 0, outcome == "capped" ? 1 : 0, 0),
        new RunArchiveCounts(attempt, 0, 0, outcome == "capped" ? 1 : 0, 0),
        new RunArchiveUsage(100, 25, 0, 0, 1200, 0.01m, "USD", "priced"),
        [],
        new RunArchiveCap(1000, null, outcome == "capped" ? "reached" : "not-configured",
            outcome == "capped" ? "Token cap reached." : null),
        new RunArchiveEstimateDeviation(-5.1m, 2.3m, null),
        ["2026-08"],
        new RunArchiveAttemptSummary(85, "B", null, 1, "medium"));

    private static string CreateTempRepository()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);
        return repositoryRoot;
    }

    private static void Delete(string repositoryRoot)
    {
        try
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Quality Studio repository root was not found from the test output directory.");
    }
}
