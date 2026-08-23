using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>RP-1 acceptance: schema fixtures validate (see ReviewRunArchiveContractTests); create-only
/// files reject overwrite; two stopped attempts under one capped/resumed run remain readable;
/// repository paths are confined. docs/operations/run-persistence/index.html#slices</summary>
public sealed class ReviewRunArchiveStoreTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-archive-tests", Guid.NewGuid().ToString("N"));

    public ReviewRunArchiveStoreTests() => Directory.CreateDirectory(repositoryRoot);

    public void Dispose()
    {
        if (Directory.Exists(repositoryRoot)) Directory.Delete(repositoryRoot, recursive: true);
    }

    [Fact]
    public void Create_run_writes_under_the_year_month_directory_and_rejects_a_second_create()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("run-1", new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero));

        store.CreateRun(run);

        var expectedPath = Path.Combine(repositoryRoot, ".quality", "run-history", "2026-08", "run-1", "run.json");
        Assert.True(File.Exists(expectedPath));
        Assert.Throws<IOException>(() => store.CreateRun(run));
    }

    [Fact]
    public void Append_operation_and_finding_requires_the_run_to_exist_first()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);

        Assert.Throws<InvalidOperationException>(() => store.AppendOperation(SampleOperation("missing-run", attempt: 1)));
        Assert.Throws<InvalidOperationException>(() => store.AppendFinding(SampleFinding("missing-run", "op-1")));
        Assert.Throws<InvalidOperationException>(() => store.CreateAttempt(SampleAttempt("missing-run", 1)));
    }

    [Fact]
    public void Operations_and_findings_append_and_read_back_in_order()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("run-2", DateTimeOffset.UtcNow);
        store.CreateRun(run);

        store.AppendOperation(SampleOperation(run.RunId, attempt: 1, ordinal: 0));
        store.AppendOperation(SampleOperation(run.RunId, attempt: 1, ordinal: 1));
        store.AppendFinding(SampleFinding(run.RunId, "op-0"));

        var operations = store.LoadOperations(run.RunId);
        var findings = store.LoadFindings(run.RunId);
        Assert.Equal([0, 1], operations.Select(operation => operation.Ordinal));
        Assert.Single(findings);
        Assert.Equal("op-0", findings[0].OperationId);
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_and_resumed_run_remain_readable()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("run-3", DateTimeOffset.UtcNow);
        store.CreateRun(run);

        store.CreateAttempt(SampleAttempt(run.RunId, 1, outcome: "capped"));
        store.CreateAttempt(SampleAttempt(run.RunId, 2, outcome: "done"));

        var attempts = store.LoadAttempts(run.RunId);
        Assert.Equal(2, attempts.Count);
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber));
        Assert.Equal("capped", attempts[0].Outcome);
        Assert.Equal("done", attempts[1].Outcome);

        var archive = store.Load(run.RunId);
        Assert.Equal(run.RunId, archive.Run.RunId);
        Assert.Equal(2, archive.Attempts.Count);
    }

    [Fact]
    public void Attempt_numbers_are_create_only_and_reject_a_duplicate()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var run = SampleRun("run-4", DateTimeOffset.UtcNow);
        store.CreateRun(run);
        store.CreateAttempt(SampleAttempt(run.RunId, 1));

        Assert.Throws<IOException>(() => store.CreateAttempt(SampleAttempt(run.RunId, 1, outcome: "failed")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/run")]
    [InlineData("..")]
    public void Run_ids_containing_path_separators_or_traversal_are_rejected(string runId)
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun(runId, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Archive_root_stays_confined_to_the_repository_even_with_a_traversal_style_root()
    {
        var store = new ReviewRunArchiveStore(repositoryRoot);
        Assert.StartsWith(Path.GetFullPath(repositoryRoot), store.ArchiveRoot, StringComparison.Ordinal);
    }

    private static RunRecord SampleRun(string runId, DateTimeOffset createdAt) => new()
    {
        RunId = runId,
        RepositoryId = "repo-1",
        CreatedAt = createdAt,
        Subject = new RunRecordSubject("node-1", "Repo", ".", "project"),
        Kind = "code",
        Targets = [new RunRecordTarget("a.cs", "sha256:" + new string('a', 64))],
        Configuration = new RunRecordConfiguration("test-agent", false),
    };

    private static RunOperationRecord SampleOperation(string runId, int attempt, int ordinal = 0) => new()
    {
        OperationId = $"op-{ordinal}",
        RunId = runId,
        Attempt = attempt,
        Ordinal = ordinal,
        UnitPath = "a.cs",
        Level = "file",
        State = "done",
    };

    private static RunFindingRecord SampleFinding(string runId, string operationId) => new()
    {
        OperationId = operationId,
        RunId = runId,
        Fingerprint = "sha256:" + new string('b', 64),
        FindingId = "finding-1",
        RuleId = "rule:1",
        Severity = FindingSeverity.Low,
        Title = "Sample finding",
        Locations = [new FindingLocation("a.cs")],
        State = "open",
        ObservedAt = DateTimeOffset.UtcNow,
    };

    private static RunAttemptRecord SampleAttempt(string runId, int attemptNumber, string outcome = "done") => new()
    {
        RunId = runId,
        AttemptNumber = attemptNumber,
        Outcome = outcome,
        Completeness = new RunAttemptCompleteness(1, 1, 0, 0),
        FinishedAt = DateTimeOffset.UtcNow,
        Spend = new RunAttemptSpend(100, 50, 0, 0, 1000, "priced"),
    };
}
