using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityDataRootTests
{
    [Fact]
    public void ResolveProjectPath_UsesConfiguredBaseAndStableProjectId()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "quality-data-resolution", Guid.NewGuid().ToString("N"));

        var resolved = QualityDataRoot.ResolveProjectPath("orders-api", "runtime-data", contentRoot);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "runtime-data", "orders-api")), resolved);
    }

    [Fact]
    public void ResolveProjectPath_RejectsPathLikeProjectId()
    {
        Assert.Throws<ArgumentException>(() => QualityDataRoot.ResolveProjectPath("../orders"));
    }

    [Fact]
    public void RepositoryProjectId_IsStableAndSeparatesSameRegistryIdAtDifferentRoots()
    {
        var first = QualityDataRoot.RepositoryProjectId("default", Path.Combine(Path.GetTempPath(), "repo-a"));

        Assert.Equal(first, QualityDataRoot.RepositoryProjectId("default", Path.Combine(Path.GetTempPath(), "repo-a")));
        Assert.NotEqual(first, QualityDataRoot.RepositoryProjectId("default", Path.Combine(Path.GetTempPath(), "repo-b")));
        Assert.StartsWith("default-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MapCheckoutPath_PreservesRepositoryRelativeShadowLayout()
    {
        var repository = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "checkout"));
        var data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-data"));

        var mapped = QualityDataRoot.MapCheckoutPath(repository, data,
            Path.Combine(repository, "src", ".quality", "reviews", "files", "file.review-meta.code.json"));

        Assert.Equal(Path.Combine(data, "src", ".quality", "reviews", "files", "file.review-meta.code.json"), mapped);
    }

    [Fact]
    public void Migrate_MovesRootAndNestedQualityTreesWithoutLosingRelativePaths()
    {
        var repository = Path.Combine(Path.GetTempPath(), "quality-migration-checkout", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(Path.GetTempPath(), "quality-migration-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repository, ".quality", "findings"));
        Directory.CreateDirectory(Path.Combine(repository, "src", "Feature", ".quality", "reviews", "files"));
        File.WriteAllText(Path.Combine(repository, ".quality", "findings", "state.json"), "root-state");
        File.WriteAllText(Path.Combine(repository, "src", "Feature", ".quality", "reviews", "files", "file.review-meta.code.json"),
            "nested-review");
        try
        {
            var result = QualityDataMigrator.Migrate(repository, data);

            Assert.Equal(2, result.FilesMoved);
            Assert.False(result.AlreadyCompleted);
            Assert.False(Directory.EnumerateDirectories(repository, ".quality", SearchOption.AllDirectories).Any());
            Assert.Equal("root-state", File.ReadAllText(Path.Combine(data, ".quality", "findings", "state.json")));
            Assert.Equal("nested-review", File.ReadAllText(Path.Combine(data, "src", "Feature", ".quality", "reviews", "files",
                "file.review-meta.code.json")));
            Assert.True(QualityDataMigrator.Migrate(repository, data).AlreadyCompleted);
        }
        finally
        {
            TestDirectory.Delete(repository);
            TestDirectory.Delete(data);
        }
    }

    [Fact]
    public void Migrate_CopiesTrackedQualityDataWithoutChangingGitStatus()
    {
        var repository = Path.Combine(Path.GetTempPath(), "quality-migration-git", Guid.NewGuid().ToString("N"));
        var data = Path.Combine(Path.GetTempPath(), "quality-migration-git-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repository, ".quality", "usage"));
        File.WriteAllText(Path.Combine(repository, ".quality", "usage", "2026-09.jsonl"), "tracked-ledger");
        try
        {
            RunGit(repository, "init", "--quiet");
            RunGit(repository, "add", ".quality/usage/2026-09.jsonl");
            var before = RunGit(repository, "status", "--porcelain=v1");

            var result = QualityDataMigrator.Migrate(repository, data);

            Assert.Equal(0, result.FilesMoved);
            Assert.Equal(1, result.TrackedFilesCopied);
            Assert.True(File.Exists(Path.Combine(repository, ".quality", "usage", "2026-09.jsonl")));
            Assert.Equal("tracked-ledger", File.ReadAllText(Path.Combine(data, ".quality", "usage", "2026-09.jsonl")));
            Assert.Equal(before, RunGit(repository, "status", "--porcelain=v1"));
        }
        finally
        {
            TestDirectory.Delete(repository);
            TestDirectory.Delete(data);
        }
    }

    private static string RunGit(string directory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}
