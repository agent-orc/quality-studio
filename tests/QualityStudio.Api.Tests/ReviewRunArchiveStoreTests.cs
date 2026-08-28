using System.Text.Json;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly Lazy<JsonSchema> RunRecordSchema = new(() => LoadSchema("run-record.v1.schema.json"));
    private static readonly Lazy<JsonSchema> RunOperationSchema = new(() => LoadSchema("run-operation.v1.schema.json"));
    private static readonly Lazy<JsonSchema> RunFindingSchema = new(() => LoadSchema("run-finding.v1.schema.json"));
    private static readonly Lazy<JsonSchema> RunAttemptSchema = new(() => LoadSchema("run-attempt.v1.schema.json"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Archive_documents_validate_against_their_v1_schema_fixtures()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");

        ValidateAgainst(RunRecordSchema.Value, SampleRun(createdAt));
        ValidateAgainst(RunOperationSchema.Value, SampleOperation());
        ValidateAgainst(RunFindingSchema.Value, SampleFinding());
        ValidateAgainst(RunAttemptSchema.Value, SampleAttempt(1, "done"));
    }

    [Fact]
    public void Run_record_is_create_only_and_rejects_a_second_write()
    {
        var repositoryRoot = CreateTempRepositoryRoot();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");
            var record = SampleRun(createdAt);

            store.CreateRun(record);
            Assert.Throws<IOException>(() => store.CreateRun(record));

            var loaded = store.LoadRun(record.RunId, createdAt);
            Assert.Equal(record.RunId, loaded.RunId);
            Assert.Equal(record.RepositoryId, loaded.RepositoryId);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var repositoryRoot = CreateTempRepositoryRoot();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");
            var record = SampleRun(createdAt);
            store.CreateRun(record);

            store.CreateAttempt(record.RunId, createdAt, SampleAttempt(1, "capped"));
            store.CreateAttempt(record.RunId, createdAt, SampleAttempt(2, "done"));

            var attempts = store.LoadAttempts(record.RunId, createdAt);

            Assert.Equal(2, attempts.Count);
            Assert.Equal(1, attempts[0].Attempt);
            Assert.Equal("capped", attempts[0].Outcome);
            Assert.Equal(2, attempts[1].Attempt);
            Assert.Equal("done", attempts[1].Outcome);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void An_attempt_ordinal_is_create_only_and_rejects_a_second_write()
    {
        var repositoryRoot = CreateTempRepositoryRoot();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");
            var record = SampleRun(createdAt);
            store.CreateRun(record);
            store.CreateAttempt(record.RunId, createdAt, SampleAttempt(1, "capped"));

            Assert.Throws<IOException>(() => store.CreateAttempt(record.RunId, createdAt, SampleAttempt(1, "done")));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void Operations_and_findings_append_and_read_back_in_order()
    {
        var repositoryRoot = CreateTempRepositoryRoot();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");
            var record = SampleRun(createdAt);
            store.CreateRun(record);

            store.AppendOperation(record.RunId, createdAt, SampleOperation() with { Ordinal = 0 });
            store.AppendOperation(record.RunId, createdAt, SampleOperation() with { Ordinal = 1 });
            store.AppendFinding(record.RunId, createdAt, SampleFinding());

            var operations = store.LoadOperations(record.RunId, createdAt);
            var findings = store.LoadFindings(record.RunId, createdAt);

            Assert.Equal([0, 1], operations.Select(operation => operation.Ordinal));
            Assert.Single(findings);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/run")]
    public void A_run_id_containing_path_separators_is_rejected(string runId)
    {
        var repositoryRoot = CreateTempRepositoryRoot();
        try
        {
            var store = new ReviewRunArchiveStore(repositoryRoot);
            var createdAt = DateTimeOffset.Parse("2026-08-11T09:30:00Z");

            Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(createdAt) with { RunId = runId }));
            Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "..", "escape")));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static void ValidateAgainst<T>(JsonSchema schema, T document)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        using var parsed = JsonDocument.Parse(json);
        var result = schema.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName)));

    private static RunRecord SampleRun(DateTimeOffset createdAt) => new(
        RunId: $"run-{Guid.NewGuid():N}",
        RepositoryId: "default",
        CreatedAt: createdAt,
        Node: new RunRecordNode("node-1", "Sample", "."),
        Level: "project",
        Kind: "code",
        Model: "runner-default",
        ThinkingLevel: "model-default",
        CliType: "test-agent",
        Force: false,
        Targets: [new RunRecordTarget("target-1", "Sample.cs", "Sample.cs",
            "sha256:" + new string('a', 64))],
        TokenCap: 50_000,
        CostCap: null,
        Estimate: new RunRecordEstimate(1, 1, 1000, 500, 0.05m, "USD", 3, "historical-average"),
        SourceRevision: new RunRecordSourceRevision("abc1234", Dirty: false));

    private static RunOperationRecord SampleOperation() => new(
        RunId: "run-1",
        OperationId: "op-1",
        Ordinal: 0,
        Attempt: 1,
        UnitId: "unit-1",
        Path: "Sample.cs",
        Level: "file",
        State: "done",
        StartedAt: DateTimeOffset.Parse("2026-08-11T09:30:05Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-11T09:30:20Z"),
        ProviderRunId: "provider-run-1",
        ReviewedHash: "sha256:" + new string('b', 64),
        InputHash: "sha256:" + new string('c', 64),
        ResultSidecarPath: "Sample.cs.review-meta.code.json",
        Verdict: new RunOperationVerdict("grade", new RunOperationGrade(88, "B", "Solid, minor issues."), null));

    private static RunFindingRecord SampleFinding() => new(
        RunId: "run-1",
        OperationId: "op-1",
        ObservedAt: DateTimeOffset.Parse("2026-08-11T09:30:20Z"),
        Fingerprint: "sha256:" + new string('d', 64),
        Id: "finding-1",
        RuleId: "QS-100",
        Severity: "medium",
        Title: "Missing null check",
        State: "open",
        Locations: [new RunFindingLocation("Sample.cs", 10, 1, 12, 5)]);

    private static RunAttemptRecord SampleAttempt(int attempt, string outcome) => new(
        RunId: "run-1",
        Attempt: attempt,
        Outcome: outcome,
        Completeness: outcome == "done" ? "complete" : "partial",
        StartedAt: DateTimeOffset.Parse("2026-08-11T09:30:00Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-11T09:31:00Z"),
        AttemptCounters: new RunAttemptCounters(3, 2, 0, 1),
        CumulativeCounters: new RunAttemptCounters(3, 2, 0, 1),
        AttemptSpend: new RunAttemptSpend(2, 1000, 500, 0, 0, 4200, 0.05m, "USD", "priced"),
        CumulativeSpend: new RunAttemptSpend(2, 1000, 500, 0, 0, 4200, 0.05m, "USD", "priced"),
        ErrorCodes: [],
        Cap: new RunAttemptCap(50_000, null, "reached", "Token cap of 50,000 reached."),
        EstimateDeviation: new RunAttemptEstimateDeviation(5.2m, -1.1m, 0m),
        LedgerReferences: ["2026-08"],
        QualitySummary: new RunAttemptQualitySummary(88, "B", new RunAttemptFindingCounts(1,
            new Dictionary<string, int> { ["medium"] = 1 }, new Dictionary<string, int> { ["open"] = 1 }), "medium", null));

    private static string CreateTempRepositoryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
