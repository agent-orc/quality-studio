using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSmokeTests
{
    [Fact]
    public async Task Directory_and_canonical_namespace_reviews_at_the_same_path_keep_separate_metadata()
    {
        await WriteExplorerFixture();
        const string path = "frontend/src/app/shell";
        var roots = RepositoryHierarchyBuilder.Build(repositoryRoot);
        var canonical = roots.SelectMany(project => project.Children).SelectMany(module => module.Children)
            .Single(node => node.Level == ReviewLevel.Namespace && node.Path == path);
        var scope = DirectoryReviewScopes.Build(roots)[path];
        var runner = new ReviewRunner(new DirectoryTestAgent());
        var canonicalResult = await runner.ReviewAsync(ScopeRequest(canonical, directoryScope: false),
            TestContext.Current.CancellationToken);
        var canonicalBytes = await File.ReadAllBytesAsync(canonicalResult.MetaPath, TestContext.Current.CancellationToken);

        var directoryResult = await runner.ReviewAsync(ScopeRequest(scope, directoryScope: true),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(canonical.Id, scope.Id);
        Assert.NotEqual(canonicalResult.MetaPath, directoryResult.MetaPath);
        Assert.Equal(canonicalBytes, await File.ReadAllBytesAsync(canonicalResult.MetaPath, TestContext.Current.CancellationToken));
        ReviewMetaDiscovery.AttachDiscovered(repositoryRoot, roots);
        var loaded = DirectoryReviewScopes.Load(repositoryRoot, roots, new InputResolver(), null,
            InputResolver.DefaultBudgetCharacters);
        Assert.Equal(ReviewState.Current, canonical.Documents[ReviewKind.Code].State);
        Assert.Equal(ReviewState.Current, loaded[path].Documents[ReviewKind.Code].State);

        using var index = new ReviewMetaIndex();
        var access = new RepositoryAccess(repositoryRoot, index);
        Assert.Equal(canonical.Id, Assert.Single(access.ReadMetaDocuments(path)).GetProperty("unit").GetProperty("id").GetString());
        Assert.Equal(scope.Id, Assert.Single(access.ReadMetaDocuments(path, unitId: scope.Id)).GetProperty("unit").GetProperty("id").GetString());
        Assert.Equal(canonicalResult.MetaPath, access.FindMetaDocument(path, "code"));
        Assert.Equal(directoryResult.MetaPath, access.FindMetaDocument(path, "code", scope.Id));
    }

    [Fact]
    public async Task Directory_metadata_stays_in_one_location_when_the_first_member_changes()
    {
        await WriteExplorerFixture();
        const string path = "frontend";
        var roots = RepositoryHierarchyBuilder.Build(repositoryRoot);
        var scope = DirectoryReviewScopes.Build(roots)[path];
        var first = scope.Children.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var second = first.Reverse().ToArray();
        Assert.NotEqual(Path.GetDirectoryName(first[0].Path), Path.GetDirectoryName(second[0].Path));
        var request = ScopeRequest(scope, directoryScope: true);
        var runner = new ReviewRunner(new DirectoryTestAgent());
        var firstResult = await runner.ReviewAsync(request with { SubjectFiles = first.Select(file => file.Path).ToArray() },
            TestContext.Current.CancellationToken);
        var secondRequest = request with { SubjectFiles = second.Select(file => file.Path).ToArray() };
        var secondResult = await runner.ReviewAsync(secondRequest, TestContext.Current.CancellationToken);

        Assert.Equal(firstResult.MetaPath, secondResult.MetaPath);
        Assert.Single(ReviewMetaPath.Enumerate(repositoryRoot));
        var loaded = DirectoryReviewScopes.Load(repositoryRoot, roots, new InputResolver(), null,
            InputResolver.DefaultBudgetCharacters);
        Assert.Equal(ReviewState.Current, loaded[path].Documents[ReviewKind.Code].State);
        var repeated = await runner.ReviewIfNeededAsync(secondRequest, false, TestContext.Current.CancellationToken);
        Assert.True(repeated.SkippedFresh);
    }

    private ReviewRequest ScopeRequest(HierarchyNode node, bool directoryScope) => new(node.Path,
        Level: ReviewLevel.Namespace, RepositoryRoot: repositoryRoot, UnitId: node.Id,
        SubjectFiles: node.Children.Select(file => file.Path).ToArray(),
        SubjectUnits: node.Children.Select(file => new ReviewSubjectFile(file.Id, file.Path)).ToArray(),
        AggregateExclusions: node.Exclusions, DirectoryScope: directoryScope);
}
