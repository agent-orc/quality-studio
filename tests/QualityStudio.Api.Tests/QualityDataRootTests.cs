using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class QualityDataRootTests
{
    [Fact]
    public void Resolver_uses_configured_base_and_project_identity()
    {
        using var directory = new TemporaryDirectory();
        var environment = new TestEnvironment(directory.Path);
        var resolver = new QualityDataRoot(environment,
            Options.Create(new RepositoryOptions { DataRoot = "runtime-data" }));

        Assert.Equal(Path.Combine(directory.Path, "runtime-data"), resolver.ProjectsRoot);
        Assert.Equal(Path.Combine(directory.Path, "runtime-data", "orders-api"),
            resolver.ForProject("orders-api"));
    }

    [Fact]
    public void Resolver_defaults_to_local_application_data()
    {
        using var directory = new TemporaryDirectory();
        var resolver = new QualityDataRoot(new TestEnvironment(directory.Path),
            Options.Create(new RepositoryOptions()));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        Assert.Equal(Path.Combine(local, "QualityStudio", "projects"), resolver.ProjectsRoot);
    }

    [Fact]
    public void Migration_moves_root_and_per_folder_quality_trees_without_losing_layout()
    {
        using var checkout = new TemporaryDirectory();
        using var data = new TemporaryDirectory();
        var rootLedger = Path.Combine(checkout.Path, ".quality", "usage", "2026-09.jsonl");
        var sidecar = Path.Combine(checkout.Path, "src", ".quality", "reviews", "files",
            "file.sample.review-meta.code.json");
        Directory.CreateDirectory(Path.GetDirectoryName(rootLedger)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
        File.WriteAllText(rootLedger, "ledger");
        File.WriteAllText(sidecar, "sidecar");

        var result = new QualityDataMigrator(NullLogger<QualityDataMigrator>.Instance)
            .Migrate(checkout.Path, data.Path);

        Assert.Equal(2, result.FilesMoved);
        Assert.False(Directory.Exists(Path.Combine(checkout.Path, ".quality")));
        Assert.False(Directory.Exists(Path.Combine(checkout.Path, "src", ".quality")));
        Assert.Equal("ledger", File.ReadAllText(Path.Combine(data.Path, ".quality", "usage", "2026-09.jsonl")));
        Assert.Equal("sidecar", File.ReadAllText(Path.Combine(data.Path, "src", ".quality", "reviews", "files",
            "file.sample.review-meta.code.json")));
        Assert.True(File.Exists(Path.Combine(data.Path, ".migration-v1.json")));

        Assert.True(new QualityDataMigrator(NullLogger<QualityDataMigrator>.Instance)
            .Migrate(checkout.Path, data.Path).AlreadyCompleted);
    }

    [Fact]
    public void Dirty_check_reports_only_in_checkout_quality_changes()
    {
        using var checkout = new TemporaryDirectory();
        RunGit(checkout.Path, "init", "--quiet");
        Directory.CreateDirectory(Path.Combine(checkout.Path, "src", ".quality"));
        File.WriteAllText(Path.Combine(checkout.Path, "src", ".quality", "state.json"), "{}");
        File.WriteAllText(Path.Combine(checkout.Path, "source.cs"), "class Source;");

        var dirty = QualityDataMigrator.DirtyQualityPaths(checkout.Path);

        Assert.Single(dirty);
        Assert.Contains("src/.quality/state.json", dirty[0], StringComparison.Ordinal);
    }

    private static void RunGit(string root, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TestEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quality-data-root-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
