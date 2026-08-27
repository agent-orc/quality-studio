using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Archived_run_operation_finding_and_attempt_records_validate_against_their_schemas()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-schema-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("schema-check");
            store.CreateRun(run);

            var operation = CreateOperation();
            store.AppendOperation(run.RunId, operation);

            var finding = CreateFinding(operation.OperationId);
            store.AppendFinding(run.RunId, finding);

            var attempt = CreateAttempt(1, "capped");
            store.WriteAttempt(run.RunId, attempt);

            AssertValid("run-record.v1.schema.json", run, ReviewRunArchiveJson.Options);
            AssertValid("run-operation.v1.schema.json", operation, ReviewRunArchiveJson.Options);
            AssertValid("run-finding.v1.schema.json", finding, ReviewRunArchiveJson.Options);
            AssertValid("run-attempt.v1.schema.json", attempt, ReviewRunArchiveJson.Options);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Create_only_files_reject_overwrite()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-createonly-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("dup-run");
            store.CreateRun(run);
            Assert.Throws<IOException>(() => store.CreateRun(run));

            var attempt = CreateAttempt(1, "done");
            store.WriteAttempt(run.RunId, attempt);
            Assert.Throws<IOException>(() => store.WriteAttempt(run.RunId, attempt));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-attempts-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("capped-then-resumed");
            store.CreateRun(run);
            store.WriteAttempt(run.RunId, CreateAttempt(1, "capped"));
            store.WriteAttempt(run.RunId, CreateAttempt(2, "done"));

            var loaded = store.LoadRun(run.RunId);

            Assert.Equal(run.RunId, loaded.Run.RunId);
            Assert.Equal([1, 2], loaded.Attempts.Select(attempt => attempt.Attempt).ToArray());
            Assert.Equal("capped", loaded.Attempts[0].Outcome);
            Assert.Equal("done", loaded.Attempts[1].Outcome);
            Assert.NotNull(loaded.LatestAttempt);
            Assert.Equal(2, loaded.LatestAttempt!.Attempt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Operations_and_findings_survive_an_incomplete_trailing_jsonl_record()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-crash-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("crash-tail");
            store.CreateRun(run);
            var first = CreateOperation();
            store.AppendOperation(run.RunId, first);

            store.TryFindRunDirectory(run.RunId, out var directory);
            File.AppendAllText(Path.Combine(directory!, "operations.jsonl"), "{\"operationId\":");

            var loaded = store.LoadRun(run.RunId);
            Assert.Single(loaded.Operations);
            Assert.Equal(first.OperationId, loaded.Operations[0].OperationId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("nested/child")]
    public void Repository_paths_are_confined_against_a_traversing_run_id(string maliciousRunId)
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-confine-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun("valid") with { RunId = maliciousRunId }));
            Assert.Throws<ArgumentException>(() => store.LoadRun(maliciousRunId));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Loading_an_unknown_run_throws_directory_not_found()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-missing-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            Assert.Throws<DirectoryNotFoundException>(() => store.LoadRun("never-created"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ReviewRunArchiveRecord CreateRun(string suffix) => new(
        RunId: $"review-{suffix}-{Guid.NewGuid():N}",
        RepositoryId: "default",
        CreatedAt: DateTimeOffset.Parse("2026-08-27T10:00:00Z"),
        UnitId: "file-sample",
        Level: "file",
        Path: "Sample.cs",
        Kind: "code",
        Model: "claude-sonnet-5",
        CliType: "test-agent",
        ThinkingLevel: "high",
        Force: false,
        Targets: [new ReviewRunArchiveTarget(
            "file-sample", "Sample.cs", "Sample.cs",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
        TargetsManifestHash: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        TokenCap: 1000,
        CostCap: 5.0m,
        Estimate: new ReviewRunArchiveEstimate(1, 1, 100, 200, 0.01m, "USD", "priced"),
        SourceRevision: new ReviewRunArchiveSourceRevision("3e0b6559faa700183a16e2ffda1a522fd75c9aa6", false));

    private static ReviewRunArchiveOperationRecord CreateOperation() => new(
        OperationId: $"op-{Guid.NewGuid():N}",
        Ordinal: 0,
        Attempt: 1,
        UnitId: "file-sample",
        Path: "Sample.cs",
        Level: "file",
        State: "done",
        StartedAt: DateTimeOffset.Parse("2026-08-27T10:00:01Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-27T10:00:02Z"),
        ProviderRunId: "provider-run-1",
        ReviewedHash: "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc11",
        SidecarPath: "Sample.cs.review.json",
        SidecarSha256: "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd11",
        Verdict: new ReviewRunArchiveVerdict("grade", new ReviewRunArchiveGrade(91, "A"), null));

    private static ReviewRunArchiveFindingRecord CreateFinding(string operationId) => new(
        OperationId: operationId,
        Fingerprint: "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
        FindingId: "finding-1",
        RuleId: "quality.rule.0",
        Severity: "medium",
        Title: "Example finding",
        Locations: [new ReviewRunArchiveLocation("Sample.cs", 10, 1, 12, 1)],
        State: "open");

    private static ReviewRunArchiveAttemptRecord CreateAttempt(int attempt, string outcome) => new(
        Attempt: attempt,
        Outcome: outcome,
        Completeness: outcome == "done" ? "complete" : "partial",
        StartedAt: DateTimeOffset.Parse("2026-08-27T10:00:00Z"),
        FinishedAt: DateTimeOffset.Parse("2026-08-27T10:05:00Z"),
        Counters: new ReviewRunArchiveCounters(1, outcome == "done" ? 1 : 0, 0, outcome == "done" ? 0 : 1),
        CumulativeCounters: new ReviewRunArchiveCounters(1, attempt, 0, 0),
        Usage: new TokenUsage(100, 200, 0, 0, 500),
        Cost: 0.02m,
        Currency: "USD",
        PriceStatus: "priced",
        ErrorCodes: [],
        Cap: new ReviewRunArchiveCap(1000, 5.0m, outcome == "capped" ? "reached" : "within-cap", null),
        EstimateDeviation: new ReviewRunArchiveEstimateDeviation(1.5m, -2.0m, null),
        UsageLedgerMonths: ["2026-08"],
        QualitySummary: new ReviewRunArchiveQualitySummary(new ReviewRunArchiveGrade(91, "A"), null, 1, "medium"),
        ArchivedAt: DateTimeOffset.Parse("2026-08-27T10:05:01Z"));

    private static void AssertValid<T>(string schemaFileName, T value, JsonSerializerOptions options)
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", schemaFileName)));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, options));
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Quality Studio repository root (QualityStudio.slnx) was not found.");
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
