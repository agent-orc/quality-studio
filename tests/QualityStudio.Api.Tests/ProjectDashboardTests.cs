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
            Assert.Empty(dashboard.Hotspots);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Complexity_projection_reads_thresholds_and_latest_preflight_breaches()
    {
        var root = TemporaryRepository();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "CodeMetricsConfig.txt"),
                "# FORMAT: 2\nCA1502: 25\nCA1505: 20\n", TestContext.Current.CancellationToken);
            var run = Path.Combine(root, ".quality", "runs", "review-fixture");
            Directory.CreateDirectory(run);
            await File.WriteAllTextAsync(Path.Combine(run, "preflight.json"), """
                {
                  "results": [{
                    "findings": [
                      {
                        "ruleId": "CA1502",
                        "title": "CA1502: method complexity",
                        "description": "'Example.Run()' has a cyclomatic complexity of '31'. Rewrite or refactor the code to decrease its complexity below '26'.",
                        "fingerprint": "sha256:ca1502",
                        "locations": [{"path":"src/Example.cs","range":{"start":{"line":12,"column":1},"end":{"line":12,"column":1}}}]
                      },
                      {
                        "ruleId": "complexity",
                        "title": "complexity: Function has a complexity of 23.",
                        "description": "Function 'render' has a complexity of 23. Maximum allowed is 18.",
                        "fingerprint": "sha256:eslint",
                        "locations": [{"path":"frontend/src/render.ts","range":{"start":{"line":7,"column":1},"end":{"line":7,"column":1}}}]
                      },
                      {
                        "ruleId": "CA1505",
                        "title": "CA1505: assembly maintainability",
                        "description": "'Example' has a maintainability index of '7'. Rewrite or refactor the code to increase its maintainability index above '19'.",
                        "fingerprint": "sha256:ca1505",
                        "locations": [{"path":"src/Example.cs"}]
                      }
                    ]
                  }]
                }
                """, TestContext.Current.CancellationToken);

            var dashboard = new ProjectDashboardService().Get(root, new RepositoryHierarchyCache().Get(root));

            Assert.Equal(25, dashboard.Metrics.Complexity.Thresholds["CA1502"]);
            Assert.Equal(18, dashboard.Metrics.Complexity.Thresholds["complexity"]);
            Assert.Equal(3, dashboard.Metrics.Complexity.TopBreaches.Count);
            Assert.Equal("Example", dashboard.Metrics.Complexity.TopBreaches[0].Symbol);
            Assert.Equal(13, dashboard.Metrics.Complexity.TopBreaches[0].Excess);
            Assert.Equal("CA1505", dashboard.Metrics.Complexity.TopBreaches[0].RuleId);
            Assert.Equal(6, dashboard.Metrics.Complexity.TopBreaches.Single(breach =>
                breach.RuleId == "CA1502").Excess);
            Assert.Equal(3, dashboard.Metrics.Complexity.BreachDistribution.Sum(bucket => bucket.Count));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    [Trait("Category", "MachineBound")]
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
            Assert.Equal(Enum.GetValues<ReviewKind>().Length, dashboard.Grades.Count);
            Assert.All(dashboard.Grades, grade => Assert.Equal("missing", grade.State));
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
