using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ProjectDashboardTests
{
    [Fact]
    public async Task Structural_metrics_match_known_fixture_repository()
    {
        var root = TemporaryRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "App"));
            Directory.CreateDirectory(Path.Combine(root, "Core"));
            await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Core", "Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
            const string duplicate = "namespace Shared;\n// deterministic duplicate fixture\npublic sealed class Marker {}\n";
            await File.WriteAllTextAsync(Path.Combine(root, "App", "Marker.cs"), duplicate,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "Core", "Marker.cs"), duplicate,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "coverage.xml"),
                "<coverage lines-covered=\"7\" lines-valid=\"10\" />", TestContext.Current.CancellationToken);

            var hierarchy = new RepositoryHierarchyCache().Get(root);
            var dashboard = new ProjectDashboardService().Get(root, hierarchy);

            Assert.Equal(5, dashboard.Metrics.FileCount);
            Assert.Equal(2, dashboard.Metrics.FolderCount);
            Assert.Equal(7, dashboard.Metrics.Lines);
            Assert.Equal(5, dashboard.Metrics.FileSizeDistribution[0].Count);
            Assert.Equal(2, dashboard.Metrics.FolderSizeDistribution[0].Count);
            var csharp = Assert.Single(dashboard.Metrics.Languages, language => language.Language == "C#");
            Assert.Equal(2, csharp.Files);
            Assert.Equal(6, csharp.Lines);
            Assert.Equal(2, Assert.Single(dashboard.Metrics.DuplicationCandidates).Paths.Count);
            var edge = Assert.Single(dashboard.Metrics.DependencyEdges);
            Assert.Equal("App", edge.Source);
            Assert.Equal("Core", edge.Target);
            Assert.Equal("project-reference", edge.Kind);
            Assert.Equal("reported", dashboard.TestCoverage.Status);
            Assert.Equal(70, dashboard.TestCoverage.LinePercent);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cached_dashboard_for_5000_file_repository_is_within_interaction_budget()
    {
        var root = TemporaryRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            for (var index = 0; index < 5_000; index++)
                await File.WriteAllTextAsync(Path.Combine(root, "src", $"file-{index:D4}.txt"), "fixture\n",
                    TestContext.Current.CancellationToken);
            RunGit(root, "add", ".");

            var snapshot = new RepositoryHierarchyCache().Get(root);
            var service = new ProjectDashboardService();
            var firstStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var dashboard = service.Get(root, snapshot);
            firstStopwatch.Stop();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var cached = service.Get(root, snapshot);

            stopwatch.Stop();
            Assert.Equal(5_000, dashboard.Metrics.FileCount);
            Assert.True(firstStopwatch.ElapsedMilliseconds < 150,
                $"Initial dashboard projection took {firstStopwatch.ElapsedMilliseconds} ms; QS-8 first-visible budget is 150 ms.");
            Assert.Same(dashboard, cached);
            Assert.True(stopwatch.ElapsedMilliseconds < 150,
                $"Cached dashboard took {stopwatch.ElapsedMilliseconds} ms; QS-8 first-visible budget is 150 ms.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Architecture_context_uses_only_declared_project_edges()
    {
        var root = TemporaryRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            File.WriteAllText(Path.Combine(root, "A", "A.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(root, "B", "B.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(root, "A", "A.cs"), "namespace A; public class AType {}");
            File.WriteAllText(Path.Combine(root, "B", "B.cs"), "namespace B; public class BType {}");
            var snapshot = new RepositoryHierarchyCache().Get(root);

            var context = new ProjectDashboardService().ArchitectureReviewContext(root, snapshot);

            Assert.Contains("id \"architecture\"", context, StringComparison.Ordinal);
            Assert.Contains("A -> B (project-reference)", context, StringComparison.Ordinal);
            Assert.Contains("do not infer edges", context, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string TemporaryRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-studio-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            ArgumentList = { "init", "--quiet" },
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return root;
    }

    private static void RunGit(string root, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
