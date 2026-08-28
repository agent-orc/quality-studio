using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("run-record.v1.schema.json", "run-record.v1.json", typeof(RunRecord))]
    [InlineData("run-operation.v1.schema.json", "run-operation.v1.json", typeof(RunOperationRecord))]
    [InlineData("run-finding.v1.schema.json", "run-finding.v1.json", typeof(RunFindingRecord))]
    [InlineData("run-attempt.v1.schema.json", "run-attempt.v1.json", typeof(RunAttemptRecord))]
    public void Fixtures_validate_against_their_schema_and_round_trip(string schemaFile, string sampleFile, Type recordType)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot(), "schemas", schemaFile)));
        var json = File.ReadAllText(Path.Combine(RepositoryRoot(), "samples", sampleFile));

        AssertValid(schema, json);

        var value = JsonSerializer.Deserialize(json, recordType, JsonOptions);
        Assert.NotNull(value);
        var roundTrip = JsonSerializer.Serialize(value, recordType, JsonOptions);

        AssertValid(schema, roundTrip);
    }

    [Fact]
    public void Create_run_rejects_a_second_write_for_the_same_run_id()
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());
        var run = SampleRun("review-duplicate");

        store.CreateRun(run);

        Assert.Throws<IOException>(() => store.CreateRun(run));
    }

    [Fact]
    public void Create_run_writes_under_the_year_month_folder_derived_from_created_at()
    {
        var repositoryRoot = CreateTempRepository();
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("review-month", createdAt: new DateTimeOffset(2026, 3, 5, 8, 0, 0, TimeSpan.Zero));

        store.CreateRun(run);

        Assert.True(File.Exists(Path.Combine(repositoryRoot, ".quality", "run-history", "2026-03", "review-month", "run.json")));
    }

    [Fact]
    public void Operations_and_findings_append_and_are_readable_in_order()
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());
        var run = SampleRun("review-append");
        store.CreateRun(run);

        store.AppendOperation(SampleOperation(run.RunId, "op-1", ordinal: 0));
        store.AppendOperation(SampleOperation(run.RunId, "op-2", ordinal: 1));
        store.AppendFinding(SampleFinding(run.RunId, "op-1"));

        var archive = store.Load(run.RunId);

        Assert.Equal(["op-1", "op-2"], archive.Operations.Select(operation => operation.OperationId));
        Assert.Equal("op-1", Assert.Single(archive.Findings).OperationId);
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_both_remain_readable()
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());
        var run = SampleRun("review-resumed");
        store.CreateRun(run);

        store.WriteAttempt(SampleAttempt(run.RunId, attempt: 1, outcome: "capped", completeness: "partial"));
        store.WriteAttempt(SampleAttempt(run.RunId, attempt: 2, outcome: "done", completeness: "complete"));

        var archive = store.Load(run.RunId);

        Assert.Equal(2, archive.Attempts.Count);
        Assert.Equal("capped", archive.Attempts[0].Outcome);
        Assert.Equal("done", archive.Attempts[1].Outcome);
    }

    [Fact]
    public void Writing_the_same_attempt_number_twice_is_rejected()
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());
        var run = SampleRun("review-duplicate-attempt");
        store.CreateRun(run);
        store.WriteAttempt(SampleAttempt(run.RunId, attempt: 1, outcome: "capped", completeness: "partial"));

        Assert.Throws<IOException>(() =>
            store.WriteAttempt(SampleAttempt(run.RunId, attempt: 1, outcome: "failed", completeness: "partial")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/id")]
    public void Run_ids_that_contain_path_separators_are_rejected(string runId)
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());

        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(runId)));
    }

    [Fact]
    public void Loading_an_unknown_run_reports_a_typed_error_instead_of_an_empty_result()
    {
        var store = new ReviewRunArchiveStore(CreateTempRepository());

        Assert.Throws<DirectoryNotFoundException>(() => store.Load("review-never-created"));
    }

    private static void AssertValid(JsonSchema schema, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = schema.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    private static string CreateTempRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static RunRecord SampleRun(string runId, DateTimeOffset? createdAt = null) => new()
    {
        RunId = runId,
        RepositoryId = "default",
        Node = new ReviewRunPlanNode("file-sample", "Sample.cs", "Sample.cs"),
        Level = "file",
        Kind = "code",
        CliType = "codex",
        Targets = [new ReviewRunPlanTarget("file-sample", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64))],
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private static RunOperationRecord SampleOperation(string runId, string operationId, int ordinal) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Attempt = 1,
        Ordinal = ordinal,
        Path = "Sample.cs",
        Level = "file",
        State = "done",
    };

    private static RunFindingRecord SampleFinding(string runId, string operationId) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Fingerprint = "sha256:" + new string('b', 64),
        FindingId = "missing-cancellation-token",
        RuleId = "async:cancellation",
        Severity = "medium",
        Title = "Cancellation is not propagated",
        Locations = [new FindingLocation("Sample.cs")],
        State = "open",
        ObservedAt = DateTimeOffset.UtcNow,
    };

    private static RunAttemptRecord SampleAttempt(string runId, int attempt, string outcome, string completeness) => new()
    {
        RunId = runId,
        Attempt = attempt,
        Outcome = outcome,
        Completeness = completeness,
        ArchivedAt = DateTimeOffset.UtcNow,
        TotalFiles = 2,
        CompletedFiles = 1,
        FailedFiles = 0,
        SkippedFiles = 1,
        Usage = new TokenUsage(1000, 200, 0, 0, 4000),
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
