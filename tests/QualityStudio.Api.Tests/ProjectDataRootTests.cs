using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ProjectDataRootTests
{
    [Fact]
    public void Resolve_UsesConfiguredBaseAndProjectIdentity()
    {
        var fixture = TemporaryDirectory();
        var repository = Path.Combine(fixture, "checkout");
        Directory.CreateDirectory(repository);
        try
        {
            var resolver = new ProjectDataRoot(new HostEnvironment(fixture),
                Options.Create(new RepositoryOptions { DataRoot = Path.Combine(fixture, "state") }));

            Assert.Equal(Path.Combine(fixture, "state", ProjectDataRoot.ProjectDirectoryName("payments", repository)),
                resolver.Resolve("payments", repository));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void MigrateOnce_MovesRootAndNestedQualityTreesAndIsIdempotent()
    {
        var fixture = TemporaryDirectory();
        var repository = Path.Combine(fixture, "checkout");
        var dataRoot = Path.Combine(fixture, "state", "sample");
        Directory.CreateDirectory(Path.Combine(repository, ".quality", "usage"));
        Directory.CreateDirectory(Path.Combine(repository, "src", ".quality", "reviews", "files"));
        File.WriteAllText(Path.Combine(repository, ".quality", "usage", "2026-09.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(repository, "src", ".quality", "reviews", "files", "sample.review-meta.code.json"), "{}\n");
        try
        {
            var resolver = new ProjectDataRoot(new HostEnvironment(fixture),
                Options.Create(new RepositoryOptions { DataRoot = Path.Combine(fixture, "state") }));

            var migrated = resolver.MigrateOnce(repository, dataRoot);
            var repeated = resolver.MigrateOnce(repository, dataRoot);

            Assert.Equal(2, migrated.MovedFiles);
            Assert.Equal(0, repeated.MovedFiles);
            Assert.False(Directory.Exists(Path.Combine(repository, ".quality")));
            Assert.False(Directory.Exists(Path.Combine(repository, "src", ".quality")));
            Assert.True(File.Exists(Path.Combine(dataRoot, ".quality", "usage", "2026-09.jsonl")));
            Assert.True(File.Exists(Path.Combine(dataRoot, "src", ".quality", "reviews", "files", "sample.review-meta.code.json")));
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    [Fact]
    public void MigrateOnce_CopiesTrackedLegacyDataWithoutDirtyingGit()
    {
        var fixture = TemporaryDirectory();
        var repository = Path.Combine(fixture, "checkout");
        var dataRoot = Path.Combine(fixture, "state", "sample");
        Directory.CreateDirectory(Path.Combine(repository, ".quality", "findings"));
        File.WriteAllText(Path.Combine(repository, ".quality", "findings", "state.json"), "{}\n");
        try
        {
            RunGit(repository, "init", "--quiet");
            RunGit(repository, "config", "user.email", "quality-studio-tests@example.invalid");
            RunGit(repository, "config", "user.name", "Quality Studio Tests");
            RunGit(repository, "add", ".quality/findings/state.json");
            RunGit(repository, "commit", "--quiet", "-m", "legacy data");
            var resolver = new ProjectDataRoot(new HostEnvironment(fixture),
                Options.Create(new RepositoryOptions { DataRoot = Path.Combine(fixture, "state") }));

            var migration = resolver.MigrateOnce(repository, dataRoot);

            Assert.Equal(1, migration.PreservedTrackedFiles);
            Assert.True(File.Exists(Path.Combine(repository, ".quality", "findings", "state.json")));
            Assert.True(File.Exists(Path.Combine(dataRoot, ".quality", "findings", "state.json")));
            Assert.Equal(string.Empty, RunGit(repository, "status", "--porcelain").Trim());
        }
        finally
        {
            Directory.Delete(fixture, true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "quality-data-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class HostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
