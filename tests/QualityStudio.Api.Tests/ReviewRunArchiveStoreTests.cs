using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Fact]
    public void Create_run_writes_an_immutable_run_record_and_rejects_a_second_write()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);
        var record = NewRunRecord("run-1");

        store.CreateRun(record);

        Assert.True(File.Exists(Path.Combine(store.ArchiveRoot, "2026-08", "run-1", "run.json")));
        Assert.Throws<IOException>(() => store.CreateRun(record));
        Assert.Throws<IOException>(() => store.CreateRun(record with { RepositoryId = "changed" }));
    }

    [Fact]
    public void Operations_and_findings_append_without_disturbing_earlier_lines()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);
        store.CreateRun(NewRunRecord("run-1"));

        store.AppendOperation(NewOperation("run-1", ordinal: 0));
        store.AppendOperation(NewOperation("run-1", ordinal: 1));
        store.AppendFinding(NewFinding("run-1", findingId: "finding-1"));

        var loaded = store.LoadRun("run-1")!;
        Assert.Equal(2, loaded.Operations.Count);
        Assert.Equal(0, loaded.Operations[0].Ordinal);
        Assert.Equal(1, loaded.Operations[1].Ordinal);
        Assert.Equal("finding-1", Assert.Single(loaded.Findings).FindingId);
    }

    [Fact]
    public void Appending_an_operation_before_the_run_record_exists_fails_loudly()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.Throws<InvalidOperationException>(() => store.AppendOperation(NewOperation("missing-run", 0)));
    }

    [Fact]
    public void A_capped_then_resumed_run_keeps_both_stopped_attempts_readable_and_ordered()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);
        store.CreateRun(NewRunRecord("run-1"));

        store.WriteAttempt(NewAttempt("run-1", attemptNumber: 1, outcome: "capped"));
        store.WriteAttempt(NewAttempt("run-1", attemptNumber: 2, outcome: "done"));

        var loaded = store.LoadRun("run-1")!;
        Assert.Equal(2, loaded.Attempts.Count);
        Assert.Equal(1, loaded.Attempts[0].AttemptNumber);
        Assert.Equal("capped", loaded.Attempts[0].Outcome);
        Assert.Equal(2, loaded.Attempts[1].AttemptNumber);
        Assert.Equal("done", loaded.Attempts[1].Outcome);
    }

    [Fact]
    public void An_attempt_number_cannot_be_rewritten()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);
        store.CreateRun(NewRunRecord("run-1"));
        store.WriteAttempt(NewAttempt("run-1", attemptNumber: 1, outcome: "capped"));

        Assert.Throws<IOException>(() => store.WriteAttempt(NewAttempt("run-1", attemptNumber: 1, outcome: "done")));
    }

    [Fact]
    public void Loading_a_run_that_was_never_archived_returns_null()
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.Null(store.LoadRun("never-archived"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Run_ids_that_escape_the_archive_root_are_rejected(string runId)
    {
        using var fixture = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.Throws<ArgumentException>(() => store.CreateRun(NewRunRecord(runId)));
    }

    [Fact]
    public void A_symlinked_month_directory_cannot_be_used_to_escape_the_archive_root()
    {
        using var fixture = new TempRepository();
        using var outside = new TempRepository();
        var store = new ReviewRunArchiveStore(fixture.Root);
        Directory.CreateDirectory(store.ArchiveRoot);
        Directory.CreateSymbolicLink(Path.Combine(store.ArchiveRoot, "2026-08"), outside.Root);

        Assert.Throws<ArgumentException>(() => store.CreateRun(NewRunRecord("run-1")));
    }

    private static RunRecord NewRunRecord(string runId) => new(
        runId,
        "quality-studio",
        new RunArchiveNode("qs-v1/dotnet/module/" + new string('a', 64), "src/QualityStudio.Api"),
        "module",
        "code",
        DateTimeOffset.Parse("2026-08-27T10:15:00Z"),
        [new RunArchiveTarget("src/QualityStudio.Api/ReviewJobs.cs", "sha256:" + new string('a', 64))],
        new RunArchiveConfiguration("claude-sonnet-5", "codex", "high", false),
        new RunArchiveSourceRevision(false, "3e0b6559faa700183a16e2ffda1a522fd75c9aa6"));

    private static RunOperation NewOperation(string runId, int ordinal) => new(
        runId,
        $"{runId}:0001:{ordinal:D4}",
        ordinal,
        1,
        "qs-v1/dotnet/file/" + new string('e', 64),
        "src/QualityStudio.Api/ReviewJobs.cs",
        "file",
        "done",
        DateTimeOffset.Parse("2026-08-27T10:15:42Z"),
        RunOperationVerdict.ForGrade(new GradeSnapshot(88, "B")));

    private static RunFinding NewFinding(string runId, string findingId) => new(
        runId,
        $"{runId}:0001:0000",
        findingId,
        "sha256:" + new string('e', 64),
        "quality.correctness.cancellation",
        "medium",
        "Cancellation is not propagated",
        [new FindingLocation("src/QualityStudio.Api/ReviewJobs.cs")],
        "open",
        DateTimeOffset.Parse("2026-08-27T10:15:42Z"));

    private static RunAttempt NewAttempt(string runId, int attemptNumber, string outcome)
    {
        var tokens = new TokenUsage(6000, 2000, 0, 0, 15000);
        return new RunAttempt(
            runId,
            attemptNumber,
            outcome,
            outcome == "capped" ? "partial" : "complete",
            DateTimeOffset.Parse("2026-08-27T10:16:00Z"),
            new RunArchiveCounters(2, 1, 0, 1),
            new RunArchiveCounters(2, 1, 0, 1),
            new RunArchiveSpend(tokens, "priced", 0.61m, "USD"),
            new RunArchiveSpend(tokens, "priced", 0.61m, "USD"),
            [],
            new RunArchiveCap(TokenCap: 8000),
            ["2026-08"],
            new RunArchiveQualitySummary(1, new GradeSnapshot(88, "B"), null, "medium"));
    }

    private sealed class TempRepository : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("qs-run-archive-tests-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
