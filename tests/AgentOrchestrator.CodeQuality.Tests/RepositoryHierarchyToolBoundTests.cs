using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class RepositoryHierarchyToolBoundTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-studio-{Guid.NewGuid():N}");

    [Fact]
    public void CacheReusesGitStateAndInvalidatesOnWorktreeContent()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), "print(1)\n");
        GitTestRepository.Initialize(root);
        var cache = new RepositoryHierarchyCache();

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
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
