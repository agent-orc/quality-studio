using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Create_run_round_trips_and_rejects_overwrite()
    {
        using var repository = new TempRepository();
        var store = new ReviewRunArchiveStore(repository.Root);
        var record = SampleRun("run-1", new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero));

        store.CreateRun(record);

        var loaded = store.Load("run-1");
        Assert.Equal(record.RunId, loaded.Run.RunId);
        Assert.Equal(record.RepositoryId, loaded.Run.RepositoryId);
        Assert.Equal(RunArchiveRecord.SchemaId, loaded.Run.Schema);
        Assert.Empty(loaded.Operations);
        Assert.Empty(loaded.Findings);
        Assert.Empty(loaded.Attempts);
        Assert.True(Directory.Exists(Path.Combine(repository.Root, ".quality", "run-history", "2026-08", "run-1")));

        Assert.Throws<IOException>(() => store.CreateRun(record));
    }

    [Fact]
    public void Operations_and_findings_append_in_order_and_survive_reload()
    {
        using var repository = new TempRepository();
        var store = new ReviewRunArchiveStore(repository.Root);
        var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        store.CreateRun(SampleRun("run-2", createdAt));

        store.AppendOperation(SampleOperation("run-2", "Sample.cs", ordinal: 0));
        store.AppendOperation(SampleOperation("run-2", "Other.cs", ordinal: 1));
        store.AppendFinding(SampleFinding("run-2", "Sample.cs"));

        var loaded = store.Load("run-2");
        Assert.Equal(["Sample.cs", "Other.cs"], loaded.Operations.Select(operation => operation.Path));
        Assert.Equal([0, 1], loaded.Operations.Select(operation => operation.Ordinal));
        var finding = Assert.Single(loaded.Findings);
        Assert.Equal("Sample.cs", Assert.Single(finding.Locations).Path);
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        using var repository = new TempRepository();
        var store = new ReviewRunArchiveStore(repository.Root);
        var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
        store.CreateRun(SampleRun("run-3", createdAt));

        store.WriteAttempt(SampleAttempt("run-3", attemptNumber: 1, outcome: "capped"));
        store.WriteAttempt(SampleAttempt("run-3", attemptNumber: 2, outcome: "done"));

        var loaded = store.Load("run-3");
        Assert.Equal(2, loaded.Attempts.Count);
        Assert.Equal("capped", loaded.Attempts[0].Outcome);
        Assert.Equal("done", loaded.Attempts[1].Outcome);

        Assert.Throws<IOException>(() => store.WriteAttempt(SampleAttempt("run-3", attemptNumber: 1, outcome: "done")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/id")]
    public void Run_ids_with_path_separators_are_rejected(string runId)
    {
        using var repository = new TempRepository();
        var store = new ReviewRunArchiveStore(repository.Root);
        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(runId, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Appending_to_an_unknown_run_throws()
    {
        using var repository = new TempRepository();
        var store = new ReviewRunArchiveStore(repository.Root);
        Assert.Throws<DirectoryNotFoundException>(() => store.AppendOperation(SampleOperation("missing-run", "Sample.cs", ordinal: 0)));
    }

    [Fact]
    public void Documents_validate_against_their_v1_schemas()
    {
        var run = SampleRun("run-4", new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero));
        var operation = SampleOperation("run-4", "Sample.cs", ordinal: 0);
        var finding = SampleFinding("run-4", "Sample.cs");
        var attempt = SampleAttempt("run-4", attemptNumber: 1, outcome: "done");

        AssertValid("run-record.v1.schema.json", run);
        AssertValid("run-operation.v1.schema.json", operation);
        AssertValid("run-finding.v1.schema.json", finding);
        AssertValid("run-attempt.v1.schema.json", attempt);
    }

    private static void AssertValid<T>(string schemaFileName, T document)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document, options));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot(), "schemas", schemaFileName)));
        var result = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Quality Studio repository root (QualityStudio.slnx) was not found above the test output directory.");
    }

    private static RunArchiveRecord SampleRun(string runId, DateTimeOffset createdAt) => new(
        RunArchiveRecord.SchemaId,
        RunArchiveRecord.CurrentSchemaVersion,
        runId,
        "default",
        createdAt,
        new ReviewRunPlanNode("root", "Sample", "."),
        "file",
        "code",
        [new ReviewRunPlanTarget("Sample.cs", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64))],
        "claude-sonnet-5",
        "high",
        "test-agent",
        false,
        5000,
        null,
        new ReviewRunEstimate(1, 1, 400, 100, 20, 0.01m, "USD", "priced", 3, "history-average"));

    private static RunOperationRecord SampleOperation(string runId, string path, int ordinal) => new(
        RunOperationRecord.SchemaId,
        RunOperationRecord.CurrentSchemaVersion,
        $"{runId}#1#{path}",
        runId,
        1,
        ordinal,
        path,
        path,
        "file",
        "done",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        "provider-run-1",
        "sha256:" + new string('b', 64),
        "sha256:" + new string('c', 64),
        $".quality/reviews/{path}.meta.json",
        new RunOperationVerdict("grade", GradeScore: 92, GradeBand: "A"));

    private static RunFindingRecord SampleFinding(string runId, string path) => new(
        RunFindingRecord.SchemaId,
        RunFindingRecord.CurrentSchemaVersion,
        $"{runId}#1#{path}",
        runId,
        "sha256:" + new string('d', 64),
        "finding-1",
        "quality.rule.0",
        "medium",
        "Sample finding",
        "open",
        [new RunFindingLocation(path, 1, 1, 1, 10)],
        DateTimeOffset.UtcNow);

    private static RunAttemptRecord SampleAttempt(string runId, int attemptNumber, string outcome) => new(
        RunAttemptRecord.SchemaId,
        RunAttemptRecord.CurrentSchemaVersion,
        runId,
        attemptNumber,
        outcome,
        outcome == "done" ? "complete" : "partial",
        DateTimeOffset.UtcNow,
        1,
        0,
        outcome == "capped" ? 1 : 0,
        0,
        new TokenUsage(100, 20, 0, 0, 500),
        "priced",
        [],
        new RunAttemptCap(5000, null, outcome == "capped" ? "reached" : "within-cap"),
        ["2026-08"]);

    private sealed class TempRepository : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));

        public TempRepository() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
