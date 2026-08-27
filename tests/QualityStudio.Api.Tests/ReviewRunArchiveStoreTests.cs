using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-08-11T10:15:00Z");

    [Theory]
    [InlineData("run-record.v1.schema.json")]
    [InlineData("run-operation.v1.schema.json")]
    [InlineData("run-finding.v1.schema.json")]
    [InlineData("run-attempt.v1.schema.json")]
    public void Schema_fixtures_validate(string schemaFileName)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", schemaFileName)));

        var (json, options) = schemaFileName switch
        {
            "run-record.v1.schema.json" =>
                (JsonSerializer.Serialize(CreateRun().WithSchema(), ReviewRunArchiveJson.DocumentOptions), ReviewRunArchiveJson.DocumentOptions),
            "run-operation.v1.schema.json" =>
                (JsonSerializer.Serialize(CreateOperation().WithSchema(), ReviewRunArchiveJson.LineOptions), ReviewRunArchiveJson.LineOptions),
            "run-finding.v1.schema.json" =>
                (JsonSerializer.Serialize(CreateFinding().WithSchema(), ReviewRunArchiveJson.LineOptions), ReviewRunArchiveJson.LineOptions),
            "run-attempt.v1.schema.json" =>
                (JsonSerializer.Serialize(CreateAttempt(1, "capped").WithSchema(), ReviewRunArchiveJson.DocumentOptions), ReviewRunArchiveJson.DocumentOptions),
            _ => throw new InvalidOperationException(),
        };
        _ = options;

        using var document = JsonDocument.Parse(json);
        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void CreateRun_is_create_only_and_rejects_a_second_write_for_the_same_run()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            store.CreateRun(CreateRun());

            Assert.Throws<IOException>(() => store.CreateRun(CreateRun()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteAttempt_is_create_only_and_rejects_a_second_write_for_the_same_attempt_number()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            store.CreateRun(CreateRun());
            store.WriteAttempt(CreateAttempt(1, "capped"));

            Assert.Throws<IOException>(() => store.WriteAttempt(CreateAttempt(1, "done")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            store.CreateRun(CreateRun());
            store.WriteAttempt(CreateAttempt(1, "capped"));
            store.WriteAttempt(CreateAttempt(2, "done"));
            store.AppendOperation(CreateOperation());
            store.AppendFinding("run-1", CreateFinding());

            var archive = store.TryLoadRun("run-1");

            Assert.NotNull(archive);
            Assert.Equal("run-1", archive!.Run.RunId);
            Assert.Equal(2, archive.Attempts.Count);
            Assert.Equal(1, archive.Attempts[0].Attempt);
            Assert.Equal("capped", archive.Attempts[0].Outcome);
            Assert.Equal(2, archive.Attempts[1].Attempt);
            Assert.Equal("done", archive.Attempts[1].Outcome);
            Assert.Single(archive.Operations);
            Assert.Single(archive.Findings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryLoadRun_returns_null_for_an_unknown_run()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Null(store.TryLoadRun("does-not-exist"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Appending_to_a_run_without_an_archived_record_fails_instead_of_creating_an_orphan_directory()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<DirectoryNotFoundException>(() => store.AppendOperation(CreateOperation()));
            Assert.Throws<DirectoryNotFoundException>(() => store.AppendFinding("run-1", CreateFinding()));
            Assert.Throws<DirectoryNotFoundException>(() => store.WriteAttempt(CreateAttempt(1, "done")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".")]
    [InlineData("..")]
    public void Run_ids_containing_path_separators_are_rejected(string runId)
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun() with { RunId = runId }));
            Assert.Throws<ArgumentException>(() => store.TryLoadRun(runId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_malformed_trailing_operation_line_does_not_hide_earlier_readable_records()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            store.CreateRun(CreateRun());
            store.AppendOperation(CreateOperation());

            var operationsPath = Path.Combine(store.ArchiveRoot, "2026-08", "run-1", "operations.jsonl");
            File.AppendAllText(operationsPath, "{\"operationId\":\"op-2\",\"trunc");

            var archive = store.TryLoadRun("run-1");

            Assert.NotNull(archive);
            Assert.Single(archive!.Operations);
            Assert.Equal("op-1", archive.Operations[0].OperationId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RunArchiveRecord CreateRun() => new(
        Schema: "",
        SchemaVersion: 0,
        RunId: "run-1",
        RepositoryId: "repo-1",
        CreatedAt: CreatedAt,
        Node: new ReviewRunPlanNode("node-1", "Root", "."),
        Level: "project",
        Kind: "code",
        Model: "claude-sonnet-5",
        ThinkingLevel: "high",
        CliType: "test-agent",
        Force: false,
        Targets: [new ReviewRunPlanTarget("t1", "file.cs", "src/file.cs", "sha256:abc")],
        TokenCap: 100_000,
        CostCap: null,
        Estimate: null,
        SourceRevision: new RunArchiveSourceRevision("91de3c847e2a056ce9b4ad19dddc5259cceacb12", false));

    private static RunOperationRecord CreateOperation() => new(
        Schema: "",
        SchemaVersion: 0,
        OperationId: "op-1",
        RunId: "run-1",
        Attempt: 1,
        Ordinal: 0,
        UnitId: "t1",
        Path: "src/file.cs",
        Level: "file",
        State: "done",
        StartedAt: CreatedAt,
        FinishedAt: CreatedAt.AddSeconds(5),
        ProviderRunId: "provider-run-1",
        SubjectHash: "sha256:abc",
        ReviewedHash: "sha256:abc",
        SidecarPath: "src/file.cs.review-meta.json",
        VerdictType: "grade",
        Grade: new RunArchiveGrade(91, "A"),
        SecurityVerdict: null);

    private static RunFindingRecord CreateFinding() => new(
        Schema: "",
        SchemaVersion: 0,
        OperationId: "op-1",
        Fingerprint: "fp-1",
        FindingId: "finding-1",
        RuleId: "quality.rule.0",
        Severity: "high",
        Title: "Example finding",
        Locations: [new QualityFindingLocation("src/file.cs", 10, 1, 12, 2)],
        State: "open",
        ObservedAt: CreatedAt.AddSeconds(5));

    private static RunAttemptRecord CreateAttempt(int attempt, string outcome) => new(
        Schema: "",
        SchemaVersion: 0,
        RunId: "run-1",
        Attempt: attempt,
        Outcome: outcome,
        Completeness: outcome == "capped" ? "partial" : "complete",
        Counters: new RunAttemptCounters(Reviewed: 1, Failed: 0, Skipped: 0, Cancelled: 0),
        CumulativeCounters: new RunAttemptCounters(Reviewed: attempt, Failed: 0, Skipped: 0, Cancelled: 0),
        Spend: new TokenUsage(1000, 500, 0, 0, 2500),
        PriceStatus: "priced",
        ErrorCodes: [],
        LedgerReferences: ["2026-08"],
        ArchivedAt: CreatedAt.AddMinutes(attempt),
        StartedAt: CreatedAt,
        FinishedAt: CreatedAt.AddMinutes(attempt),
        Cost: 0.42m,
        Currency: "USD",
        TokenCap: 100_000,
        CostCap: null,
        EstimateDeviationPercent: 5.0m,
        QualitySummary: new RunAttemptQualitySummary(91, "A", null, 0, null));
}
