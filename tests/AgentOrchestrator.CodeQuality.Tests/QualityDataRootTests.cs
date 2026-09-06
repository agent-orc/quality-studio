using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityDataRootTests
{
    [Fact]
    public void ResolveProject_uses_configured_projects_root_and_stable_identity()
    {
        var configured = Path.Combine(Path.GetTempPath(), "qs-data-root-tests", Guid.NewGuid().ToString("N"));
        var first = QualityDataRoot.ResolveProject("repo-123", configured);
        var second = QualityDataRoot.ResolveProject("repo-123", configured);

        Assert.Equal(Path.Combine(Path.GetFullPath(configured), "repo-123"), first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Migration_moves_root_and_nested_quality_trees_once()
    {
        var fixture = NewFixture();
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.Repository, ".quality", "findings"));
            File.WriteAllText(Path.Combine(fixture.Repository, ".quality", "findings", "state.json"), "{}");
            Directory.CreateDirectory(Path.Combine(fixture.Repository, "src", ".quality", "reviews", "files"));
            File.WriteAllText(Path.Combine(fixture.Repository, "src", ".quality", "reviews", "files", "sample.review-meta.code.json"), "{}");

            var result = QualityDataMigration.Migrate(fixture.Repository, fixture.Data);
            var repeated = QualityDataMigration.Migrate(fixture.Repository, fixture.Data);

            Assert.True(result.Performed);
            Assert.Equal(2, result.FilesMoved);
            Assert.False(repeated.Performed);
            Assert.False(Directory.EnumerateDirectories(fixture.Repository, ".quality", SearchOption.AllDirectories).Any());
            Assert.True(File.Exists(Path.Combine(fixture.Data, ".quality", "findings", "state.json")));
            Assert.True(File.Exists(Path.Combine(fixture.Data, ".quality", "by-path", "src", "reviews", "files", "sample.review-meta.code.json")));
        }
        finally
        {
            TestDirectory.Delete(fixture.Parent);
        }
    }

    [Fact]
    public async Task Review_run_writes_no_files_below_checkout()
    {
        var fixture = NewFixture();
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture.Repository, "src"));
            var source = Path.Combine(fixture.Repository, "src", "Sample.cs");
            await File.WriteAllTextAsync(source, "internal sealed class Sample { }\n", TestContext.Current.CancellationToken);
            var before = Directory.EnumerateFiles(fixture.Repository, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);

            var result = await new ReviewRunner(new SuccessfulAgent()).ReviewAsync(
                new ReviewRequest("src/Sample.cs", RepositoryRoot: fixture.Repository, DataRoot: fixture.Data),
                TestContext.Current.CancellationToken);

            var after = Directory.EnumerateFiles(fixture.Repository, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
            Assert.True(before.SetEquals(after));
            Assert.StartsWith(Path.GetFullPath(fixture.Data), result.MetaPath, StringComparison.Ordinal);
            Assert.True(File.Exists(result.MetaPath));
            Assert.True(File.Exists(UsageLedger.GetLedgerPath(fixture.Data, result.Usage.Timestamp)));
            Assert.True(File.Exists(new FindingStateStore(fixture.Data).StatePath));
        }
        finally
        {
            TestDirectory.Delete(fixture.Parent);
        }
    }

    private static (string Parent, string Repository, string Data) NewFixture()
    {
        var parent = Path.Combine(Path.GetTempPath(), "quality-data-root-tests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(parent, "repository");
        var data = Path.Combine(parent, "data");
        Directory.CreateDirectory(repository);
        return (parent, repository, data);
    }

    private sealed class SuccessfulAgent : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => "test-model";

        public Task<ReviewAgentResult> RunAsync(string prompt, string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            var finding = ReviewResponseParserTests.ValidFinding.Replace("src/Small.cs", "src/Sample.cs", StringComparison.Ordinal);
            var response = ReviewResponseParserTests.ValidResponse.Replace("\"findings\": []", $"\"findings\": [{finding}]", StringComparison.Ordinal);
            return Task.FromResult(new ReviewAgentResult(
                "test-run", response, new TokenUsage(10, 2, 0, 0, 1), Model));
        }
    }
}
