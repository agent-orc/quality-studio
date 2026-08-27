using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset RunCreatedAt = new(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("run-record.v1.schema.json")]
    [InlineData("run-operation.v1.schema.json")]
    [InlineData("run-finding.v1.schema.json")]
    [InlineData("run-attempt.v1.schema.json")]
    public void Schema_fixtures_validate(string schemaFileName)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schemas", schemaFileName)));
        var fixtureJson = schemaFileName switch
        {
            "run-record.v1.schema.json" => Serialize(CreateRunRecord()),
            "run-operation.v1.schema.json" => Serialize(CreateOperation(attempt: 1)),
            "run-finding.v1.schema.json" => Serialize(CreateFinding()),
            "run-attempt.v1.schema.json" => Serialize(CreateAttempt(attempt: 1, outcome: "done")),
            _ => throw new InvalidOperationException(schemaFileName),
        };

        using var document = JsonDocument.Parse(fixtureJson);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Create_run_rejects_overwrite_and_confines_repository_paths()
    {
        var root = Directory.CreateTempSubdirectory("run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var record = CreateRunRecord();
            store.CreateRun(record);

            Assert.Throws<IOException>(() => store.CreateRun(record));
            Assert.Throws<ArgumentException>(() => store.CreateRun(record with { RunId = "../escape" }));
            Assert.Throws<ArgumentException>(() => store.CreateRun(record with { RunId = "nested/id" }));

            var expected = Path.Combine(store.ArchivePath, "2026-08", record.RunId, "run.json");
            Assert.True(File.Exists(expected));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var root = Directory.CreateTempSubdirectory("run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var record = CreateRunRecord();
            store.CreateRun(record);
            store.AppendOperation(RunCreatedAt, CreateOperation(attempt: 1));
            store.AppendFinding(RunCreatedAt, CreateFinding());

            var firstOrdinal = store.NextAttemptOrdinal(record.RunId, RunCreatedAt);
            Assert.Equal(1, firstOrdinal);
            store.WriteAttempt(RunCreatedAt, CreateAttempt(attempt: firstOrdinal, outcome: "capped"));

            var secondOrdinal = store.NextAttemptOrdinal(record.RunId, RunCreatedAt);
            Assert.Equal(2, secondOrdinal);
            store.AppendOperation(RunCreatedAt, CreateOperation(attempt: secondOrdinal));
            store.WriteAttempt(RunCreatedAt, CreateAttempt(attempt: secondOrdinal, outcome: "done"));

            Assert.Throws<IOException>(() =>
                store.WriteAttempt(RunCreatedAt, CreateAttempt(attempt: 1, outcome: "failed")));

            var loaded = store.TryLoad(record.RunId, RunCreatedAt);
            Assert.NotNull(loaded);
            Assert.Equal(record.RunId, loaded!.Run.RunId);
            Assert.Equal(2, loaded.Attempts.Count);
            Assert.Equal(1, loaded.Attempts[0].Attempt);
            Assert.Equal("capped", loaded.Attempts[0].Outcome);
            Assert.Equal(2, loaded.Attempts[1].Attempt);
            Assert.Equal("done", loaded.Attempts[1].Outcome);
            Assert.Equal(2, loaded.Operations.Count);
            Assert.Single(loaded.Findings);

            var found = store.TryFind(record.RunId);
            Assert.NotNull(found);
            Assert.Equal(2, found!.Attempts.Count);

            Assert.Null(store.TryFind("missing-run"));
            Assert.Null(store.TryLoad(record.RunId, RunCreatedAt.AddMonths(1)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Malformed_trailing_jsonl_record_does_not_hide_earlier_readable_records()
    {
        var root = Directory.CreateTempSubdirectory("run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var record = CreateRunRecord();
            store.CreateRun(record);
            store.AppendOperation(RunCreatedAt, CreateOperation(attempt: 1));

            var operationsPath = Path.Combine(store.ArchivePath, "2026-08", record.RunId, "operations.jsonl");
            File.AppendAllText(operationsPath, "{\"runId\":\"broken\"");

            var loaded = store.TryLoad(record.RunId, RunCreatedAt);
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Operations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static RunRecord CreateRunRecord() => new(
        RunId: "review-archive-fixture",
        RepositoryId: "repo-1",
        Node: new RunArchiveNode("unit-project", "Fixture repository", "."),
        Level: "project",
        Kind: "code",
        Model: "claude-sonnet-5",
        ThinkingLevel: "high",
        CliType: "test-agent",
        CreatedAt: RunCreatedAt,
        Targets: [new RunArchiveTarget("unit-file", "App.cs", "src/App.cs", "sha256:" + new string('a', 64))],
        Force: false,
        TokenCap: 5000,
        CostCap: null,
        SourceCommit: "3e0b6559faa700183a16e2ffda1a522fd75c9aa6",
        SourceDirty: false);

    private static RunOperationRecord CreateOperation(int attempt) => new(
        RunId: "review-archive-fixture",
        OperationId: $"op-{attempt}",
        Ordinal: 0,
        Attempt: attempt,
        Path: "src/App.cs",
        Level: "file",
        State: "done",
        StartedAt: RunCreatedAt,
        FinishedAt: RunCreatedAt.AddMinutes(1),
        ProviderRunId: "provider-run-1",
        SubjectHash: "sha256:" + new string('a', 64),
        ReviewInputHash: "sha256:" + new string('b', 64),
        SidecarPath: ".quality/reviews/files/src/App.cs.review-meta.code.json",
        SidecarSha256: "sha256:" + new string('c', 64),
        GradeScore: 85,
        GradeBand: "B",
        SecurityVerdict: null);

    private static RunFindingRecord CreateFinding() => new(
        RunId: "review-archive-fixture",
        OperationId: "op-1",
        Fingerprint: "sha256:" + new string('d', 64),
        FindingId: "finding-0",
        RuleId: "quality.rule.0",
        Severity: "medium",
        Title: "Fixture finding",
        Locations: [new RunFindingLocation("src/App.cs", 1, 1, 1, 8)],
        State: "open",
        ObservedAt: RunCreatedAt.AddMinutes(1));

    private static RunAttemptRecord CreateAttempt(int attempt, string outcome) => new(
        RunId: "review-archive-fixture",
        Attempt: attempt,
        Outcome: outcome,
        Completeness: outcome == "done" ? "complete" : "partial",
        ArchivedAt: RunCreatedAt.AddMinutes(attempt),
        TotalFiles: 1,
        CompletedFiles: outcome == "done" ? 1 : 0,
        FailedFiles: 0,
        SkippedFiles: 0,
        Usage: new TokenUsage(100, 25, 10, 5, 1200),
        PriceStatus: "priced",
        ErrorCodes: [],
        UsageLedgerMonths: ["2026-08"],
        StartedAt: RunCreatedAt,
        FinishedAt: RunCreatedAt.AddMinutes(attempt),
        CostSpent: 0.01m,
        Currency: "USD",
        TokenCap: 5000,
        CostCap: null,
        StopReason: outcome == "capped" ? "Token cap reached." : null);
}

internal static class RepositoryRoot
{
    private const string RootMarker = "QualityStudio.slnx";

    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Quality Studio repository root was not found above {AppContext.BaseDirectory}.");
    }
}
