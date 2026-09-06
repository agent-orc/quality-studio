using System.Security.Cryptography;
using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityDataRootTests
{
    [Fact]
    public void Resolve_uses_the_configured_base_and_a_repository_identity_when_unregistered()
    {
        var fixture = CreateFixture();
        try
        {
            var resolved = QualityDataRoot.Resolve(fixture.Repository);

            Assert.Equal(QualityDataRoot.ResolveBasePath(null), Path.GetDirectoryName(resolved));
            Assert.StartsWith("repository-", Path.GetFileName(resolved), StringComparison.Ordinal);
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    [Fact]
    public void Register_resolves_a_stable_project_directory_outside_the_checkout()
    {
        var fixture = CreateFixture();
        try
        {
            var resolved = QualityDataRoot.Register(fixture.Repository, "QS project", fixture.DataBase);

            Assert.Equal(Path.Combine(fixture.DataBase, "qs-project"), resolved);
            Assert.Equal(Path.Combine(resolved, "usage", "2026-09.jsonl"),
                QualityDataRoot.PathFor(fixture.Repository, ".quality/usage/2026-09.jsonl"));
            Assert.False(resolved.StartsWith(fixture.Repository + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    [Fact]
    public void Register_rejects_a_data_root_inside_the_checkout()
    {
        var fixture = CreateFixture();
        try
        {
            Assert.Throws<InvalidOperationException>(() => QualityDataRoot.Register(
                fixture.Repository, "project", Path.Combine(fixture.Repository, "runtime")));
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    [Fact]
    public void PathFor_rejects_a_logical_path_that_escapes_the_data_root()
    {
        var fixture = CreateFixture();
        try
        {
            QualityDataRoot.Register(fixture.Repository, "project", fixture.DataBase);
            Assert.Throws<ArgumentException>(() => QualityDataRoot.PathFor(
                fixture.Repository, ".quality/../../outside.json"));
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    [Fact]
    public async Task Migration_moves_root_and_distributed_quality_trees_without_losing_content()
    {
        var fixture = CreateFixture();
        var rootFinding = Path.Combine(fixture.Repository, ".quality", "findings", "state.json");
        var nestedReview = Path.Combine(fixture.Repository, "src", "Feature", ".quality", "reviews", "files",
            "feature.review-meta.code.json");
        Directory.CreateDirectory(Path.GetDirectoryName(rootFinding)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nestedReview)!);
        await File.WriteAllTextAsync(rootFinding, "finding-state", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(nestedReview, "review-metadata", TestContext.Current.CancellationToken);
        var dataRoot = QualityDataRoot.Register(fixture.Repository, "migration", fixture.DataBase);

        try
        {
            var result = await new QualityDataMigrator().MigrateAsync(
                fixture.Repository, dataRoot, TestContext.Current.CancellationToken);

            Assert.Equal(2, result.FilesMoved);
            Assert.Equal("finding-state", await File.ReadAllTextAsync(
                Path.Combine(dataRoot, "findings", "state.json"), TestContext.Current.CancellationToken));
            Assert.Equal("review-metadata", await File.ReadAllTextAsync(
                Path.Combine(dataRoot, "reviews", "files", "feature.review-meta.code.json"),
                TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".quality")));
            Assert.False(Directory.Exists(Path.Combine(fixture.Repository, "src", "Feature", ".quality")));

            var repeated = await new QualityDataMigrator().MigrateAsync(
                fixture.Repository, dataRoot, TestContext.Current.CancellationToken);
            Assert.Equal(0, repeated.FilesMoved);
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    [Fact]
    public async Task Review_run_does_not_write_under_the_checkout()
    {
        var fixture = CreateFixture();
        var sourceDirectory = Path.Combine(fixture.Repository, "src");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "Small.cs"),
            "internal static class Small { }\n", TestContext.Current.CancellationToken);
        var before = Snapshot(fixture.Repository);
        var dataRoot = QualityDataRoot.Register(fixture.Repository, "read-only-review", fixture.DataBase);

        try
        {
            var result = await new ReviewRunner(new FixedAgent()).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: fixture.Repository),
                TestContext.Current.CancellationToken);

            Assert.Equal(before, Snapshot(fixture.Repository));
            Assert.StartsWith(dataRoot, result.MetaPath, StringComparison.Ordinal);
            Assert.True(File.Exists(result.MetaPath));
            Assert.True(Directory.Exists(Path.Combine(dataRoot, "usage")));
        }
        finally
        {
            TestDirectory.Delete(fixture.Root);
        }
    }

    private static (string Root, string Repository, string DataBase) CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-data-root-tests", Guid.NewGuid().ToString("N"));
        var repository = Path.Combine(root, "repository");
        var dataBase = Path.Combine(root, "data");
        Directory.CreateDirectory(repository);
        return (root, repository, dataBase);
    }

    private static IReadOnlyDictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private sealed class FixedAgent : IReviewAgent
    {
        public string AgentName => "data-root-test";
        public string? Model => "deterministic";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                "data-root-run",
                $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(10, 5, 0, 0, 20),
                Model));
    }
}
