using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class RepositoryHierarchyMetadataCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"quality-cache-metadata-{Guid.NewGuid():N}");

    public RepositoryHierarchyMetadataCacheTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root, "Sample.cs"),
            "namespace First { class One {} }\nnamespace Second { class Two {} }\n");
        GitTestRepository.Initialize(root);
        GitTestRepository.Run(root, "add", ".");
        GitTestRepository.Run(root, "commit", "--quiet", "-m", "Cache fixture");
    }

    [Fact]
    public async Task External_review_changes_refresh_attachments_without_rebuilding_or_mutating_prior_snapshots()
    {
        var cache = new RepositoryHierarchyCache(TimeSpan.Zero);
        var original = cache.GetMeasured(root);
        var originalFiles = Files(original.Snapshot);
        Assert.Equal(2, originalFiles.Length);
        Assert.Same(originalFiles[0], originalFiles[1]);
        var file = originalFiles[0];
        var review = await new ReviewRunner(new ReviewRunnerTests.FakeAgent()).ReviewAsync(
            new ReviewRequest(file.Path, Level: ReviewLevel.File, RepositoryRoot: root,
                UnitId: file.Id, SubjectFiles: [file.Path]), TestContext.Current.CancellationToken);

        var reviewed = cache.GetMeasured(root);
        var reviewedFiles = Files(reviewed.Snapshot);
        Assert.False(reviewed.CacheHit);
        Assert.Equal(0, reviewed.ScanMilliseconds);
        Assert.NotEqual(original.Snapshot.ETag, reviewed.Snapshot.ETag);
        Assert.Same(reviewedFiles[0], reviewedFiles[1]);
        Assert.NotSame(file, reviewedFiles[0]);
        Assert.Empty(file.Documents);
        Assert.Equal(ReviewState.Current, reviewedFiles[0].Documents[ReviewKind.Code].State);

        File.Delete(review.MetaPath);
        var removed = cache.GetMeasured(root);
        Assert.Equal(0, removed.ScanMilliseconds);
        Assert.Empty(Files(removed.Snapshot)[0].Documents);
        Assert.Single(reviewedFiles[0].Documents);
        Assert.True(cache.GetMeasured(root).CacheHit);
    }

    [Fact]
    public void Review_inputs_reuse_structure_but_source_and_scope_changes_rebuild_it()
    {
        var cache = new RepositoryHierarchyCache(TimeSpan.Zero);
        var original = cache.GetMeasured(root);
        var inputs = Path.Combine(root, ".quality", "inputs");
        Directory.CreateDirectory(inputs);
        File.WriteAllText(Path.Combine(inputs, "code.md"), "Review public API boundaries.\n");
        var policyChanged = cache.GetMeasured(root);
        Assert.False(policyChanged.CacheHit);
        Assert.Equal(0, policyChanged.ScanMilliseconds);
        Assert.NotEqual(original.Snapshot.ETag, policyChanged.Snapshot.ETag);

        File.AppendAllText(Path.Combine(root, "Sample.cs"), "namespace Third { class Three {} }\n");
        var sourceChanged = cache.GetMeasured(root);
        Assert.True(sourceChanged.ScanMilliseconds > 0);
        Assert.Equal(3, Files(sourceChanged.Snapshot).Length);

        File.WriteAllText(Path.Combine(root, ".quality", "scope.json"),
            "{\"rules\":[{\"action\":\"exclude\",\"pattern\":\"Sample.cs\",\"reason\":\"Fixture exclusion\"}]}");
        var scopeChanged = cache.GetMeasured(root);
        Assert.True(scopeChanged.ScanMilliseconds > 0);
        Assert.Empty(Files(scopeChanged.Snapshot));
    }

    private static HierarchyNode[] Files(RepositoryHierarchySnapshot snapshot) =>
        snapshot.Roots.SelectMany(project => project.Children)
            .SelectMany(module => module.Children).SelectMany(ns => ns.Children)
            .Where(node => node.Level == ReviewLevel.File).ToArray();

    public void Dispose()
    {
        TemporaryDirectory.Delete(QualityDataRoot.For(root));
        TemporaryDirectory.Delete(root);
        GC.SuppressFinalize(this);
    }
}
