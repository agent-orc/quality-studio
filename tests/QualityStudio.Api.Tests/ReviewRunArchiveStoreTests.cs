using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "qs-archive-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewRunArchiveStore store;
    private readonly string monthFolder = DateTimeOffset.UtcNow.ToString("yyyy-MM");

    public ReviewRunArchiveStoreTests()
    {
        Directory.CreateDirectory(repositoryRoot);
        store = new ReviewRunArchiveStore(repositoryRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(repositoryRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Operation_id_is_deterministic_and_stable_across_recovery()
    {
        var first = ReviewRunArchiveStore.OperationId("run-1", "src/Example.cs");
        var second = ReviewRunArchiveStore.OperationId("run-1", "src/Example.cs");
        var differentPath = ReviewRunArchiveStore.OperationId("run-1", "src/Other.cs");

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentPath);
        Assert.StartsWith("op-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_run_rejects_overwrite_of_an_existing_manifest()
    {
        store.CreateRun(SampleRun("run-1"));

        Assert.Throws<IOException>(() => store.CreateRun(SampleRun("run-1")));
    }

    [Fact]
    public void Create_attempt_rejects_overwrite_of_an_existing_attempt_number()
    {
        store.CreateRun(SampleRun("run-1"));
        store.CreateAttempt(SampleAttempt("run-1", 1, "done"));

        Assert.Throws<IOException>(() => store.CreateAttempt(SampleAttempt("run-1", 1, "done")));
    }

    [Fact]
    public void Operations_and_findings_append_without_truncating_prior_entries()
    {
        store.CreateRun(SampleRun("run-1"));
        store.AppendOperation(SampleOperation("run-1", 1, "a.cs"));
        store.AppendOperation(SampleOperation("run-1", 1, "b.cs"));
        store.AppendFinding(SampleFinding("run-1", 1, "a.cs"));

        var operations = store.ReadOperations(monthFolder, "run-1");
        var findings = store.ReadFindings(monthFolder, "run-1");

        Assert.Equal(2, operations.Count);
        Assert.Equal(["a.cs", "b.cs"], operations.Select(operation => operation.Path));
        Assert.Single(findings);
    }

    [Fact]
    public void Two_stopped_attempts_under_one_capped_then_resumed_run_remain_readable()
    {
        store.CreateRun(SampleRun("run-1"));
        store.CreateAttempt(SampleAttempt("run-1", 1, "capped"));
        store.CreateAttempt(SampleAttempt("run-1", 2, "done"));

        var attempts = store.ReadAttempts(monthFolder, "run-1");

        Assert.Equal(2, attempts.Count);
        Assert.Equal(1, attempts[0].Attempt);
        Assert.Equal("capped", attempts[0].Outcome);
        Assert.Equal(2, attempts[1].Attempt);
        Assert.Equal("done", attempts[1].Outcome);
    }

    [Fact]
    public void Run_id_with_path_separators_is_refused()
    {
        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun("../escape")));
        Assert.Throws<ArgumentException>(() => store.CreateRun(SampleRun("nested/run")));
    }

    [Fact]
    public void Reads_of_a_run_id_with_path_separators_are_refused()
    {
        Assert.Throws<ArgumentException>(() => store.ReadRun(monthFolder, "../escape"));
        Assert.Throws<ArgumentException>(() => store.ReadOperations(monthFolder, "nested/run"));
    }

    [Fact]
    public void Month_folder_must_match_the_expected_shape()
    {
        Assert.Throws<ArgumentException>(() => store.ReadRun("../escape", "run-1"));
        Assert.Throws<ArgumentException>(() => store.ReadRun("2026-8", "run-1"));
    }

    [Fact]
    public void Reading_a_run_that_was_never_created_returns_null()
    {
        Assert.Null(store.ReadRun(monthFolder, "missing-run"));
        Assert.Empty(store.ReadOperations(monthFolder, "missing-run"));
        Assert.Empty(store.ReadFindings(monthFolder, "missing-run"));
        Assert.Empty(store.ReadAttempts(monthFolder, "missing-run"));
    }

    private static RunRecord SampleRun(string runId) => new(
        ReviewRunArchiveStore.RunRecordSchemaUrl, 1, runId, "quality-studio", DateTimeOffset.UtcNow,
        "code", "file", "qs-v1/generic/file/" + new string('a', 64), "src/Example.cs",
        [new RunArchiveTarget("qs-v1/generic/file/" + new string('a', 64), "Example.cs", "src/Example.cs", "sha256:" + new string('b', 64))],
        new RunArchiveConfiguration("runner-default", "model-default", "codex", false, null, null, false),
        new RunArchiveCap(null, null), null, null);

    private static RunOperationRecord SampleOperation(string runId, int attempt, string path) => new(
        ReviewRunArchiveStore.RunOperationSchemaUrl, 1, runId, attempt, ReviewRunArchiveStore.OperationId(runId, path),
        0, "qs-v1/generic/file/" + new string('a', 64), "file", path, "done",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "provider-run-1", null, null, null, null);

    private static RunFindingRecord SampleFinding(string runId, int attempt, string path) => new(
        ReviewRunArchiveStore.RunFindingSchemaUrl, 1, runId, attempt, ReviewRunArchiveStore.OperationId(runId, path),
        "sha256:" + new string('d', 64), "missing-cancellation-token", "async:cancellation", "medium",
        "Cancellation is not propagated", "open", [], DateTimeOffset.UtcNow);

    private static RunAttemptRecord SampleAttempt(string runId, int attempt, string outcome) => new(
        ReviewRunArchiveStore.RunAttemptSchemaUrl, 1, runId, attempt, outcome, "partial",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new RunAttemptCounters(new RunAttemptFileCounts(1, 1, 0, 0, 0), new RunAttemptFileCounts(1, 1, 0, 0, 0)),
        new RunAttemptUsage(1, 100, 50, 0, 0, 500, null, null, "unknownModel"),
        new RunAttemptCap(null, null, "not-configured", null),
        null, [], new RunAttemptLedger(runId, ["2026-08"], []),
        new RunAttemptQualitySummary(null, null, new Dictionary<string, int>(), null));
}
