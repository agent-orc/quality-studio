using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly Lazy<JsonSchema> RunRecordSchema = LoadSchema("run-record.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunOperationSchema = LoadSchema("run-operation.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunFindingSchema = LoadSchema("run-finding.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunAttemptSchema = LoadSchema("run-attempt.v1.schema.json");

    [Fact]
    public void ArchivedRunRecordValidatesAgainstItsSchema() =>
        AssertValid(RunRecordSchema.Value, Serialize(SampleRun("schema-record")));

    [Fact]
    public void ArchivedOperationValidatesAgainstItsSchema() =>
        AssertValid(RunOperationSchema.Value, SerializeLine(SampleOperation("op-schema", ordinal: 0, attempt: 1)));

    [Fact]
    public void ArchivedFindingValidatesAgainstItsSchema() =>
        AssertValid(RunFindingSchema.Value, SerializeLine(SampleFinding("op-schema")));

    [Fact]
    public void ArchivedAttemptValidatesAgainstItsSchema() =>
        AssertValid(RunAttemptSchema.Value, Serialize(SampleAttempt("schema-attempt", 1, "done")));

    [Fact]
    public void CreateRunIsCreateOnlyAndRejectsOverwrite()
    {
        var store = new ReviewRunArchiveStore(CreateRepositoryRoot());
        var run = SampleRun("dup-run");

        var directory = store.CreateRun(run);

        Assert.True(File.Exists(Path.Combine(directory, "run.json")));
        Assert.Throws<IOException>(() => store.CreateRun(run));
    }

    [Fact]
    public void TwoStoppedAttemptsUnderOneCappedThenResumedRunRemainReadable()
    {
        var repositoryRoot = CreateRepositoryRoot();
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("capped-resume-run");
        store.CreateRun(run);

        var first = store.WriteNextAttempt(run.RunId, next => SampleAttempt(run.RunId, next, "capped"));
        var second = store.WriteNextAttempt(run.RunId, next => SampleAttempt(run.RunId, next, "done"));

        Assert.Equal(1, first.Attempt);
        Assert.Equal(2, second.Attempt);

        var loaded = store.TryLoad(run.RunId);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Attempts.Count);
        Assert.Equal("capped", loaded.Attempts[0].Outcome);
        Assert.Equal("done", loaded.Attempts[1].Outcome);

        // A fresh store instance (simulating a new process) must find the same run purely from disk.
        var reopened = new ReviewRunArchiveStore(repositoryRoot).TryLoad(run.RunId);
        Assert.NotNull(reopened);
        Assert.Equal(2, reopened!.Attempts.Count);
    }

    [Fact]
    public void WriteNextAttemptRejectsAnAttemptThatSkipsTheCreateOnlySequence()
    {
        var store = new ReviewRunArchiveStore(CreateRepositoryRoot());
        var run = SampleRun("out-of-order-attempt");
        store.CreateRun(run);

        Assert.Throws<ArgumentException>(() =>
            store.WriteNextAttempt(run.RunId, _ => SampleAttempt(run.RunId, 5, "done")));
    }

    [Fact]
    public void WriteNextAttemptRejectsRewritingAnExistingAttemptNumber()
    {
        var store = new ReviewRunArchiveStore(CreateRepositoryRoot());
        var run = SampleRun("no-attempt-rewrite");
        store.CreateRun(run);
        store.WriteNextAttempt(run.RunId, next => SampleAttempt(run.RunId, next, "capped"));

        // A caller that races the create-only ordinal and still targets attempt 1 must be rejected,
        // not silently overwrite the immutable capped snapshot.
        Assert.Throws<ArgumentException>(() =>
            store.WriteNextAttempt(run.RunId, _ => SampleAttempt(run.RunId, 1, "done")));
    }

    [Fact]
    public void AppendedOperationsAndFindingsAreReadableAndSurviveAMalformedFinalLine()
    {
        var repositoryRoot = CreateRepositoryRoot();
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("append-run");
        store.CreateRun(run);

        store.AppendOperation(run.RunId, SampleOperation("op-1", 0, 1));
        store.AppendOperation(run.RunId, SampleOperation("op-2", 1, 1));
        store.AppendFinding(run.RunId, SampleFinding("op-1"));

        var directory = Path.Combine(store.ArchiveRoot, run.CreatedAt.UtcDateTime.ToString("yyyy-MM"), run.RunId);
        File.AppendAllText(Path.Combine(directory, "operations.jsonl"), "{\"operationId\":");

        var loaded = store.TryLoad(run.RunId);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Operations.Count);
        Assert.Equal(["op-1", "op-2"], loaded.Operations.Select(operation => operation.OperationId));
        Assert.Single(loaded.Findings);
        Assert.Equal("op-1", loaded.Findings[0].OperationId);
    }

    [Fact]
    public void AppendingToAnUnarchivedRunThrows()
    {
        var store = new ReviewRunArchiveStore(CreateRepositoryRoot());

        Assert.Throws<InvalidOperationException>(() =>
            store.AppendOperation("never-created", SampleOperation("op", 0, 1)));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/child")]
    [InlineData(".")]
    [InlineData("..")]
    public void RunIdCannotEscapeTheArchiveRoot(string maliciousRunId)
    {
        var store = new ReviewRunArchiveStore(CreateRepositoryRoot());

        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(maliciousRunId)));
    }

    [Fact]
    public void LoadAllIgnoresDirectoriesThatDoNotLookLikeArchiveMonths()
    {
        var repositoryRoot = CreateRepositoryRoot();
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("month-scoped-run");
        store.CreateRun(run);
        Directory.CreateDirectory(Path.Combine(store.ArchiveRoot, "not-a-month"));

        var all = store.LoadAll();

        Assert.Single(all);
        Assert.Equal(run.RunId, all[0].Run.RunId);
    }

    [Fact]
    public void OperationIdIsStableForTheSameRunAndPathAcrossCalls()
    {
        var first = ReviewOperationId.ForFile("run-1", "src/a.ts");
        var second = ReviewOperationId.ForFile("run-1", "src/a.ts");
        var differentPath = ReviewOperationId.ForFile("run-1", "src/b.ts");
        var differentRun = ReviewOperationId.ForFile("run-2", "src/a.ts");

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentPath);
        Assert.NotEqual(first, differentRun);
    }

    [Fact]
    public void AggregateOperationIdDiffersFromARegularFileOperationId() =>
        Assert.NotEqual(ReviewOperationId.ForAggregate("run-1"), ReviewOperationId.ForFile("run-1", "src/a.ts"));

    private static string CreateRepositoryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static RunArchiveRecord SampleRun(string runId) => new()
    {
        RunId = runId,
        RepositoryId = "default",
        CreatedAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        Node = new RunArchiveNode("file-sample", "Sample.cs", "Sample.cs"),
        Level = "file",
        Kind = "code",
        Model = "claude-sonnet-5",
        CliType = "codex",
        Targets = [new RunArchiveTarget("file-sample", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64))],
        SourceRevision = new string('b', 40),
        SourceDirty = false,
    };

    private static RunArchiveOperation SampleOperation(string operationId, int ordinal, int attempt) => new()
    {
        OperationId = operationId,
        Ordinal = ordinal,
        Attempt = attempt,
        UnitId = "file-sample",
        Path = "Sample.cs",
        Level = "file",
        State = "done",
        StartedAt = new DateTimeOffset(2026, 8, 20, 9, 0, 1, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 8, 20, 9, 0, 5, TimeSpan.Zero),
        ProviderRunId = "provider-run-1",
        ReviewedHash = "sha256:" + new string('c', 64),
        SidecarPath = ".quality/reviews/sample.review-meta.code.json",
        GradeScore = 92,
        GradeBand = GradeBand.A,
        Verdict = "A",
    };

    private static RunArchiveFinding SampleFinding(string operationId) => new()
    {
        OperationId = operationId,
        Fingerprint = "sha256:" + new string('d', 64),
        FindingId = "prefer-const-name",
        RuleId = "built-in:code",
        Severity = FindingSeverity.Info,
        Title = "Name could express intent",
        Locations = [new FindingLocation("Sample.cs")],
        State = "open",
    };

    private static RunArchiveAttempt SampleAttempt(string runId, int attempt, string outcome) => new()
    {
        RunId = runId,
        Attempt = attempt,
        Outcome = outcome,
        Completeness = outcome == "done" ? "complete" : "partial",
        StartedAt = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        FinishedAt = new DateTimeOffset(2026, 8, 20, 9, 5, 0, TimeSpan.Zero),
        TotalFiles = 1,
        CompletedFiles = 1,
        FailedFiles = 0,
        SkippedFiles = 0,
        Usage = new TokenUsage(100, 40, 10, 0, 1500),
        CostSpent = 0.05m,
        Currency = "USD",
        PriceStatus = "priced",
        UsageLedgerMonths = ["2026-08"],
        ArchivedAt = new DateTimeOffset(2026, 8, 20, 9, 5, 1, TimeSpan.Zero),
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, RunArchiveJson.Options);

    private static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, RunArchiveJson.LineOptions);

    private static void AssertValid(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static Lazy<JsonSchema> LoadSchema(string fileName) => new(() => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName))));
}
