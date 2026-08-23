using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>RP-1: operation ids and attempt ordinals in the in-memory work item, ready for RP-2 to
/// persist to the run archive. docs/operations/run-persistence/index.html#slices</summary>
public sealed class ReviewRunOperationIdentityTests
{
    [Fact]
    public void Compute_is_deterministic_for_the_same_run_attempt_and_target()
    {
        var first = ReviewRunOperationIdentity.Compute("run-1", 1, "src/a.cs");
        var second = ReviewRunOperationIdentity.Compute("run-1", 1, "src/a.cs");

        Assert.Equal(first, second);
        Assert.StartsWith("op-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Compute_differs_across_attempts_targets_and_runs()
    {
        var baseline = ReviewRunOperationIdentity.Compute("run-1", 1, "src/a.cs");

        Assert.NotEqual(baseline, ReviewRunOperationIdentity.Compute("run-1", 2, "src/a.cs"));
        Assert.NotEqual(baseline, ReviewRunOperationIdentity.Compute("run-1", 1, "src/b.cs"));
        Assert.NotEqual(baseline, ReviewRunOperationIdentity.Compute("run-2", 1, "src/a.cs"));
    }

    [Fact]
    public void Compute_rejects_an_attempt_number_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReviewRunOperationIdentity.Compute("run-1", 0, "src/a.cs"));
    }
}

public sealed class ReviewWorkItemAttemptTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-workitem-tests", Guid.NewGuid().ToString("N"));

    public ReviewWorkItemAttemptTests() => Directory.CreateDirectory(repositoryRoot);

    public void Dispose()
    {
        if (Directory.Exists(repositoryRoot)) Directory.Delete(repositoryRoot, recursive: true);
    }

    [Fact]
    public void A_capped_resume_advances_the_attempt_and_yields_a_fresh_operation_id_for_the_same_target()
    {
        var item = CreateWorkItem(tokenCap: 5);
        Assert.Equal(1, item.AttemptNumber);
        var firstAttemptOperationId = item.ArchiveOperationId("a.cs");
        Assert.Equal(firstAttemptOperationId, item.ArchiveOperationId("a.cs"));

        item.Start();
        item.StartFile("a.cs");
        item.AddUsage(new ReviewUsageEntry("provider-run-1", DateTimeOffset.UtcNow, "test-model", "test-agent",
            new TokenUsage(10, 0, 0, 0, 1), "code", "file", "a.cs"));
        item.FinishFile("a.cs", new ReviewExecutionResult(false, null, null));

        Assert.True(item.TryStopAtCap());
        Assert.Equal("capped", item.State);
        Assert.Equal(1, item.AttemptNumber);

        item.Resume(newTokenCap: 500, newCostCap: null);

        Assert.Equal(2, item.AttemptNumber);
        var secondAttemptOperationId = item.ArchiveOperationId("a.cs");
        Assert.NotEqual(firstAttemptOperationId, secondAttemptOperationId);
        Assert.Equal(secondAttemptOperationId, item.ArchiveOperationId("a.cs"));
    }

    [Fact]
    public void Pause_and_crash_recovery_do_not_advance_the_attempt_number()
    {
        var item = CreateWorkItem(tokenCap: null);
        item.Start();

        item.Pause();
        item.Resume(newTokenCap: null, newCostCap: null);
        Assert.Equal(1, item.AttemptNumber);

        item.PrepareForRecovery();
        Assert.Equal(1, item.AttemptNumber);
    }

    private ReviewJobService.ReviewWorkItem CreateWorkItem(long? tokenCap)
    {
        var store = new ReviewRunStore(repositoryRoot);
        var repository = new RepositoryRegistration("repo-1", "Repo", repositoryRoot, null, 8000, ["code"]);
        var manifest = new ReviewRunManifest(
            "run-attempt-1",
            repository.Id,
            new ReviewRunPlanNode("node-1", "Repo", "."),
            "project",
            "code",
            null,
            "test-agent",
            DateTimeOffset.UtcNow,
            [
                new ReviewRunPlanTarget("t1", "a.cs", "a.cs", "hash-a"),
                new ReviewRunPlanTarget("t2", "b.cs", "b.cs", "hash-b"),
            ],
            null,
            null,
            null,
            tokenCap);
        return ReviewJobService.ReviewWorkItem.Create(manifest, repository, store);
    }
}
