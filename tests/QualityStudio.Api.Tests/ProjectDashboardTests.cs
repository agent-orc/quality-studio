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

    [Fact]
    public async Task Observation_read_cutover_drives_dashboard_grade_findings_and_coverage()
    {
        var root = TemporaryRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "App.cs"),
                "namespace Fixture; public class App {}\n", TestContext.Current.CancellationToken);
            var hierarchy = new RepositoryHierarchyCache().Get(root);
            var file = hierarchy.Roots.SelectMany(Flatten).Single(node => node.Level == ReviewLevel.File);
            var evidence = new QualityEvidence("ev-1", QualityEvidenceKind.SourceCode,
                new QualityEvidenceLocator("src/App.cs", StartLine: 1, EndLine: 1),
                "Dashboard evidence.", null, null, null, QualityObservationJson.NoExtensions);
            var observation = new QualityObservation(
                QualityObservation.SchemaId, 1, "observation-sha256:" + new string('a', 64),
                DateTimeOffset.UtcNow,
                new QualityCatalogueReference(QualityTaxonomyCatalogue.CoreId,
                    QualityTaxonomyCatalogue.CoreVersion, QualityTaxonomyCatalogue.CoreDigest),
                null,
                new QualitySubject(file.Id, "sha256:" + new string('b', 64), "file",
                    QualityObservationJson.NoExtensions),
                new QualityProfile("file-code-review", "1.0.0", "sha256:" + new string('c', 64),
                    "sha256:" + new string('d', 64), "code", QualityObservationJson.NoExtensions),
                new QualityProducer(QualityProducerKind.Agent, "codex", "openai", "model-a", "model-a",
                    "high", "2026-07-24", "run-a", null, QualityObservationJson.NoExtensions),
                QualityEvidenceStatus.Available,
                [evidence],
                [new QualityAspectObservation("code.correctness", "Correctness", QualityAssessment.Concern, null,
                    "Dashboard score.", new QualityGrade(82, "B"), QualityObservationJson.NoExtensions)],
                QualityAssessment.Concern, null, null,
                [new QualityObservationFinding("of-1", "issue-sha256:" + new string('e', 64),
                    "sha256:" + new string('f', 64), QualityObservationIdentity.FingerprintAlgorithm, [],
                    "quality.dashboard", "code.correctness", FindingSeverity.Medium, "Dashboard finding",
                    "The dashboard should count this.", "Fix it.", ["ev-1"],
                    new QualityFindingSource(QualityProducerKind.Agent, "self"),
                    QualityObservationJson.NoExtensions)],
                null, QualityObservationJson.NoExtensions);
            await new QualityObservationStore(root).AppendAsync(observation, TestContext.Current.CancellationToken);

            var dashboard = new ProjectDashboardService(
                new QualityTaxonomyOptions { ObservationReadEnabled = true }).Get(root, hierarchy);

            var code = Assert.Single(dashboard.Grades, grade => grade.Kind == "code");
            Assert.Equal(82, code.Score);
            Assert.Equal("B", code.Band);
            Assert.Equal(1, dashboard.Findings.Open);
            Assert.Equal(1, dashboard.Findings.BySeverity["medium"]);
            Assert.Equal(100, dashboard.ReviewCoverage.Percent);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static IEnumerable<HierarchyNode> Flatten(HierarchyNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten)) yield return child;
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
