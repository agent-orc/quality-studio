using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSmokeTests
{
    [Theory]
    [InlineData("frontend/src/app", 2)]
    [InlineData("frontend", 3)]
    [InlineData(".", 5)]
    public async Task Physical_directory_review_plans_deduplicated_files_and_an_aggregate(string path, int count)
    {
        await WriteExplorerFixture();
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path,
            kind = "code",
            scopeType = "directory",
            cliType = "codex",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        var preflight = await estimate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(count, preflight.GetProperty("estimate").GetProperty("files").GetInt32());
        Assert.Equal(count + 1, preflight.GetProperty("estimate").GetProperty("operations").GetInt32());
        Assert.Equal("namespace", preflight.GetProperty("level").GetString());

        using var review = await client.PostAsJsonAsync("/api/review", new
        {
            path,
            kind = "code",
            scopeType = "directory",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
        var run = await review.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(path, run.GetProperty("path").GetString());
        Assert.Equal(count, run.GetProperty("totalFiles").GetInt32());
    }

    [Theory]
    [InlineData("missing-folder", "directory", HttpStatusCode.NotFound)]
    [InlineData("../outside", "directory", HttpStatusCode.BadRequest)]
    [InlineData("Sample.cs", "directory", HttpStatusCode.NotFound)]
    [InlineData(".", "unknown", HttpStatusCode.BadRequest)]
    public async Task Physical_directory_reviews_reject_invalid_scopes(string path, string scopeType, HttpStatusCode status)
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path,
            kind = "code",
            scopeType,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(status, estimate.StatusCode);
    }

    [Fact]
    public void Directory_scopes_preserve_file_identity_and_do_not_include_sibling_prefixes()
    {
        var a = new HierarchyNode("a", "One.cs", ReviewLevel.File, "backend/api/One.cs");
        var alias = new HierarchyNode("z", "One.cs", ReviewLevel.File, "backend/api/One.cs");
        var b = new HierarchyNode("b", "Two.cs", ReviewLevel.File, "backend/api-extra/Two.cs");
        var scopes = DirectoryReviewScopes.Build([alias, b, a]);
        Assert.Same(a, Assert.Single(scopes["backend/api"].Children));
        Assert.Equal(2, scopes["backend"].Children.Count);
        Assert.Equal(2, scopes["."].Children.Count);
        Assert.Equal(RepositoryExplorerProjection.ScopeId("backend/api"), scopes["backend/api"].Id);
        Assert.Equal(ReviewLevel.Namespace, scopes["."].Level);
    }

    [Fact]
    public async Task Directory_metadata_roundtrip_preserves_file_documents_and_detects_membership_changes()
    {
        var roots = RepositoryHierarchyBuilder.BuildDotNet(repositoryRoot);
        var original = DirectoryReviewScopes.Build(roots)["."];
        var file = Assert.Single(original.Children);
        var runner = new ReviewRunner(new DirectoryTestAgent());
        await runner.ReviewAsync(new ReviewRequest(file.Path, RepositoryRoot: repositoryRoot,
            UnitId: file.Id), TestContext.Current.CancellationToken);
        await runner.ReviewAsync(new ReviewRequest(".", Level: ReviewLevel.Namespace,
            RepositoryRoot: repositoryRoot, UnitId: original.Id, DirectoryScope: true,
            SubjectFiles: [file.Path], SubjectUnits: [new ReviewSubjectFile(file.Id, file.Path)],
            AggregateExclusions: original.Exclusions), TestContext.Current.CancellationToken);

        ReviewMetaDiscovery.AttachDiscovered(repositoryRoot, roots);
        Assert.Equal(ReviewState.Current, file.Documents[ReviewKind.Code].State);
        var loaded = DirectoryReviewScopes.Load(repositoryRoot, roots, new InputResolver(), null,
            InputResolver.DefaultBudgetCharacters);
        Assert.Equal(ReviewState.Current, loaded["."].Documents[ReviewKind.Code].State);
        Assert.Single(file.Documents);
        var index = TreeProjectionIndex.Create(roots, new Dictionary<string, FindingStateRecord>(), null, null);
        var projected = index.GetExplorer(repositoryRoot, roots, () => loaded).Root;
        Assert.Equal("fresh", projected.Kinds["code"].Direct);
        Assert.Equal(95, projected.Kinds["code"].Score);

        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Added.cs"),
            "namespace Sample; public class Added {}", TestContext.Current.CancellationToken);
        var changed = RepositoryHierarchyBuilder.BuildDotNet(repositoryRoot);
        var stale = DirectoryReviewScopes.Load(repositoryRoot, changed, new InputResolver(), null,
            InputResolver.DefaultBudgetCharacters);
        Assert.Equal(ReviewState.Stale, stale["."].Documents[ReviewKind.Code].State);
    }

    private sealed class DirectoryTestAgent : IReviewAgent
    {
        public string AgentName => "directory-test";
        public string? Model => "deterministic";
        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(new ReviewAgentResult("directory-test", """
            {
              "grade": { "score": 95, "band": "A", "rationale": "Fixture." },
              "summary": "Fixture.",
              "aspects": [{ "id": "correctness", "title": "Correctness",
                "grade": { "score": 95, "band": "A", "rationale": "Fixture." } }],
              "findings": []
            }
            """));
    }

}
