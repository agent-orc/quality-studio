using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class RepositoryHierarchyToolBoundTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-studio-{Guid.NewGuid():N}");

    [Fact]
    public void GitStateTtlThrottlesGitAndAWorkingTreeWithoutGitIsAnExplicitError()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "print(1)" + Environment.NewLine);
        var withoutGit = new RepositoryHierarchyCache(TimeSpan.Zero).GetMeasured(root).Snapshot;

        GitTestRepository.Initialize(root);
        var throttled = new RepositoryHierarchyCache(TimeSpan.FromMinutes(5));
        var first = throttled.GetMeasured(root).Snapshot;
        File.WriteAllText(Path.Combine(root, "main.py"), "print(2)" + Environment.NewLine);
        var withinTtl = throttled.GetMeasured(root);

        Assert.Equal("unavailable", withoutGit.GitStateStatus);
        Assert.StartsWith("git-unavailable", withoutGit.GitState, StringComparison.Ordinal);
        Assert.Contains("git status failed", withoutGit.GitStateDetail!, StringComparison.Ordinal);
        Assert.Equal("ok", first.GitStateStatus);
        Assert.Null(first.GitStateDetail);
        Assert.True(withinTtl.CacheHit);
        Assert.Same(first, withinTtl.Snapshot);
    }

    [Fact]
    public void CacheReusesGitStateAndInvalidatesOnWorktreeContent()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "print(1)\n");
        GitTestRepository.Initialize(root);
        // No Git-state TTL here: this asserts the invalidation itself, not the throttle in front of it.
        var cache = new RepositoryHierarchyCache(TimeSpan.Zero);

        var firstMeasurement = cache.GetMeasured(root);
        var warmMeasurement = cache.GetMeasured(root);
        var first = firstMeasurement.Snapshot;
        var warm = warmMeasurement.Snapshot;
        File.WriteAllText(Path.Combine(root, "main.py"), "print(2)\n");
        var changedMeasurement = cache.GetMeasured(root);
        var changed = changedMeasurement.Snapshot;

        Assert.False(firstMeasurement.CacheHit);
        Assert.True(warmMeasurement.CacheHit);
        Assert.Equal(0, warmMeasurement.ScanMilliseconds);
        Assert.Equal(0, warmMeasurement.ReviewMetaDiscoveryMilliseconds);
        Assert.False(changedMeasurement.CacheHit);
        Assert.Same(first, warm);
        Assert.NotSame(first, changed);
        Assert.NotEqual(first.ETag, changed.ETag);
    }

    public void Dispose()
    {
        TemporaryDirectory.Delete(root);
        GC.SuppressFinalize(this);
    }
}
