using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly JsonSchema RunRecordSchema = LoadSchema("run-record.v1.schema.json");
    private static readonly JsonSchema RunOperationSchema = LoadSchema("run-operation.v1.schema.json");
    private static readonly JsonSchema RunFindingSchema = LoadSchema("run-finding.v1.schema.json");
    private static readonly JsonSchema RunAttemptSchema = LoadSchema("run-attempt.v1.schema.json");

    [Fact]
    public void Archived_run_operation_finding_and_attempt_files_validate_against_their_schemas()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");

        store.CreateRun(CreateRun("run-schema-1", createdAt));
        AssertValid(RunRecordSchema, ReadJson(store.ArchiveRoot, "2026-08", "run-schema-1", "run.json"));

        store.AppendOperation(CreateOperation("run-schema-1", attempt: 1));
        AssertValid(RunOperationSchema, ReadJsonLine(store.ArchiveRoot, "2026-08", "run-schema-1", "operations.jsonl"));

        store.AppendFinding(CreateFinding("run-schema-1"));
        AssertValid(RunFindingSchema, ReadJsonLine(store.ArchiveRoot, "2026-08", "run-schema-1", "findings.jsonl"));

        store.CreateAttempt("run-schema-1", number => CreateAttempt("run-schema-1", number, createdAt));
        AssertValid(RunAttemptSchema, ReadJson(store.ArchiveRoot, "2026-08", "run-schema-1", "attempts", "0001.json"));
    }

    [Fact]
    public void Run_record_is_create_only_and_rejects_overwrite()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");

        store.CreateRun(CreateRun("run-create-only", createdAt));
        Assert.Throws<IOException>(() => store.CreateRun(CreateRun("run-create-only", createdAt)));
    }

    [Fact]
    public void CreateAttempt_never_overwrites_a_colliding_attempt_file_and_retries_the_next_number()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");
        store.CreateRun(CreateRun("run-collision", createdAt));

        var attemptsDirectory = Path.Combine(store.ArchiveRoot, "2026-08", "run-collision", "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        File.WriteAllText(Path.Combine(attemptsDirectory, "0001.json"), "{\"sentinel\":true}");

        var created = store.CreateAttempt("run-collision", number => CreateAttempt("run-collision", number, createdAt));

        Assert.Equal(2, created.AttemptNumber);
        Assert.Equal("{\"sentinel\":true}", File.ReadAllText(Path.Combine(attemptsDirectory, "0001.json")));
    }

    [Fact]
    public void CreateAttempt_rejects_a_factory_that_does_not_stamp_the_assigned_number()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");
        store.CreateRun(CreateRun("run-bad-factory", createdAt));

        Assert.Throws<ArgumentException>(() =>
            store.CreateAttempt("run-bad-factory", _ => CreateAttempt("run-bad-factory", attemptNumber: 99, createdAt)));
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");
        store.CreateRun(CreateRun("run-two-attempts", createdAt));

        var capped = store.CreateAttempt("run-two-attempts",
            number => CreateAttempt("run-two-attempts", number, createdAt) with { Outcome = "capped", Completeness = "partial" });
        var resumed = store.CreateAttempt("run-two-attempts",
            number => CreateAttempt("run-two-attempts", number, createdAt) with { Outcome = "done", Completeness = "complete" });

        Assert.Equal(1, capped.AttemptNumber);
        Assert.Equal(2, resumed.AttemptNumber);

        var loaded = store.Load("run-two-attempts");
        Assert.Equal(2, loaded.Attempts.Count);
        Assert.Equal(["capped", "done"], loaded.Attempts.Select(attempt => attempt.Outcome).ToArray());
        Assert.Equal("done", loaded.LatestAttempt!.Outcome);
    }

    [Fact]
    public void Repository_paths_are_confined_against_a_run_id_containing_path_separators()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:00:00Z");

        Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun("../escape", createdAt)));
        Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun("nested/child", createdAt)));
        Assert.Throws<ArgumentException>(() => store.AppendOperation(CreateOperation("../escape", attempt: 1)));
    }

    [Fact]
    public void Load_all_discovers_runs_across_month_directories()
    {
        var root = CreateTempRepository();
        var store = new ReviewRunArchiveStore(root);

        store.CreateRun(CreateRun("run-july", DateTimeOffset.Parse("2026-07-20T09:00:00Z")));
        store.CreateRun(CreateRun("run-august", DateTimeOffset.Parse("2026-08-05T09:00:00Z")));

        var loaded = store.LoadAll();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, archive => archive.Run.RunId == "run-july");
        Assert.Contains(loaded, archive => archive.Run.RunId == "run-august");
    }

    private static string CreateTempRepository() => Directory.CreateTempSubdirectory("review-run-archive-").FullName;

    private static RunArchiveRecord CreateRun(string runId, DateTimeOffset createdAt) => new(
        RunArchiveRecord.SchemaUri,
        RunArchiveRecord.CurrentSchemaVersion,
        runId,
        "default",
        createdAt,
        new RunArchiveNode("unit-1", "Sample", "src/Sample.cs"),
        Level: "file",
        Kind: "code",
        Model: "claude-sonnet-5",
        ThinkingLevel: "high",
        CliType: "test-agent",
        Force: false,
        Targets: [new RunArchiveTarget("unit-1", "Sample", "src/Sample.cs", "sha256:" + new string('a', 64))]);

    private static RunOperationRecord CreateOperation(string runId, int attempt) => new(
        RunOperationRecord.SchemaUri,
        RunOperationRecord.CurrentSchemaVersion,
        runId,
        OperationId: $"{runId}-op-1",
        Attempt: attempt,
        Ordinal: 0,
        UnitId: "unit-1",
        Path: "src/Sample.cs",
        Level: "file",
        Kind: "code",
        State: "done",
        SubjectHash: "sha256:" + new string('a', 64),
        StartedAt: DateTimeOffset.Parse("2026-08-11T09:00:00Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-11T09:00:05Z"),
        ReviewedAt: DateTimeOffset.Parse("2026-08-11T09:00:05Z"),
        Verdict: new RunArchiveVerdict(RunVerdictKind.Grade, new RunArchiveGrade(91, "A")));

    private static RunFindingRecord CreateFinding(string runId) => new(
        RunFindingRecord.SchemaUri,
        RunFindingRecord.CurrentSchemaVersion,
        runId,
        OperationId: $"{runId}-op-1",
        Fingerprint: "fp-1",
        FindingId: "finding-1",
        RuleId: "quality.rule.0",
        Severity: "medium",
        Title: "Sample finding",
        State: "open",
        ObservedAt: DateTimeOffset.Parse("2026-08-11T09:00:05Z"),
        Locations: [new RunFindingLocation("src/Sample.cs", 10, 12)]);

    private static RunAttemptRecord CreateAttempt(string runId, int attemptNumber, DateTimeOffset createdAt) => new(
        RunAttemptRecord.SchemaUri,
        RunAttemptRecord.CurrentSchemaVersion,
        runId,
        AttemptNumber: attemptNumber,
        Outcome: "done",
        Completeness: "complete",
        CreatedAt: createdAt,
        ArchivedAt: createdAt.AddSeconds(10),
        Counters: new RunAttemptCounters(TotalFiles: 1, CompletedFiles: 1, FailedFiles: 0, SkippedFiles: 0),
        Usage: new TokenUsage(100, 200, 0, 0, 500));

    private static void AssertValid(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, JsonSerializer.Serialize(result) + "\n" + json);
    }

    private static string ReadJson(params string[] segments) => File.ReadAllText(Path.Combine(segments));

    private static string ReadJsonLine(params string[] segments) =>
        File.ReadLines(Path.Combine(segments)).Single(line => !string.IsNullOrWhiteSpace(line));

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName)));
}
