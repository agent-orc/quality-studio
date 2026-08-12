using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Boundary", "Git")]
public sealed class CoverageSensorTests
{
    [Fact]
    public void Fixture_reports_from_all_supported_stacks_produce_identical_file_facts()
    {
        using var fixture = new CoverageFixture();
        var parser = new CoverageReportParser();
        var expected = Assert.Single(parser.Parse(fixture.Root, [fixture.Report("coverage.cobertura.xml")]));
        var lcov = Assert.Single(parser.Parse(fixture.Root, [fixture.Report("lcov.info")]));
        var trx = Assert.Single(parser.Parse(fixture.Root, [fixture.Report("coverage.trx")]));

        AssertCoverage(expected);
        AssertCoverage(lcov);
        AssertCoverage(trx);
    }

    [Fact]
    public async Task Missing_reports_are_persisted_and_projected_as_unknown_not_zero()
    {
        using var fixture = new CoverageFixture();
        var result = await new CoverageSensor().RunAsync(new SensorScanRequest(
            fixture.Root,
            Configuration: new Dictionary<string, string> { ["reportPaths"] = "missing.xml" }),
            TestContext.Current.CancellationToken);

        var snapshot = CoverageSnapshot.Load(fixture.Root);
        Assert.True(result.Available);
        Assert.Contains("No coverage reports", result.UnavailableReason, StringComparison.Ordinal);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Files);
        var projection = CoverageProjection.ForPath(snapshot, snapshot.Commit, "src/Calculator.cs", file: true);
        Assert.Equal("unknown", projection.State);
        Assert.Null(projection.LinePercent);
        Assert.NotEqual(0m, projection.LinePercent);
    }

    [Fact]
    public void Prompt_evidence_names_uncovered_lines_and_preserves_staleness()
    {
        var snapshot = new CoverageSnapshot(1, CoverageSensor.CurrentVersion, "2026-01-01T00:00:00Z", "old",
            ["coverage.xml"], [new CoverageFile("src/Calculator.cs", 2, 3, 1, 2, [11], [11])]);

        var evidence = CoverageProjection.Evidence(snapshot, "new", ["src/Calculator.cs"]);
        var prompt = new ReviewPromptBuilder().Build("src/Calculator.cs", "code", fileContent: "class Calculator {}",
            coverageEvidence: evidence);

        Assert.Contains("uncovered lines 11", prompt, StringComparison.Ordinal);
        Assert.Contains("uncovered branches on lines 11", prompt, StringComparison.Ordinal);
        Assert.Contains("is stale for the current commit", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_rollup_sums_reported_descendants_and_exposes_the_measured_commit()
    {
        var snapshot = new CoverageSnapshot(1, CoverageSensor.CurrentVersion, "2026-01-01T00:00:00Z", "measured",
            ["coverage.xml"],
            [
                new CoverageFile("src/A.cs", 8, 10, 0, 0, [9, 10], []),
                new CoverageFile("src/B.cs", 1, 10, 0, 0, [2, 3, 4, 5, 6, 7, 8, 9, 10], []),
            ]);

        var rollup = CoverageProjection.ForPath(snapshot, "current", "src", file: false,
            ["src/A.cs", "src/B.cs", "src/Missing.cs"]);

        Assert.Equal("stale", rollup.State);
        Assert.Equal(9, rollup.CoveredLines);
        Assert.Equal(20, rollup.TotalLines);
        Assert.Equal(45m, rollup.LinePercent);
        Assert.Equal(2, rollup.FilesWithData);
        Assert.Equal("measured", rollup.Commit);
    }

    [Fact]
    public void Churn_counts_each_commit_that_touched_a_file_in_the_configured_window()
    {
        using var fixture = new GitChurnFixture();
        fixture.Commit("2025-12-01T12:00:00Z", ("src/B.cs", "old"));
        fixture.Commit("2026-01-09T12:00:00Z", ("src/A.cs", "one"), ("src/B.cs", "one"));
        fixture.Commit("2026-01-10T12:00:00Z", ("src/A.cs", "two"));

        var churn = new GitChurnAnalyzer().Analyze(
            fixture.Root, 7, DateTimeOffset.Parse("2026-01-11T00:00:00Z"));

        Assert.Equal(2, churn["src/A.cs"]);
        Assert.Equal(1, churn["src/B.cs"]);
    }

    private static void AssertCoverage(CoverageFile file)
    {
        Assert.Equal("src/Calculator.cs", file.Path);
        Assert.Equal(2, file.CoveredLines);
        Assert.Equal(3, file.TotalLines);
        Assert.Equal(1, file.CoveredBranches);
        Assert.Equal(2, file.TotalBranches);
        Assert.Equal([11], file.UncoveredLines);
        Assert.Equal([11], file.UncoveredBranchLines);
    }

    private sealed class CoverageFixture : IDisposable
    {
        public CoverageFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-coverage-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "Calculator.cs"), "class Calculator {}\n");
            var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Coverage");
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }

        public string Root { get; }
        public string Report(string name) => Path.Combine(Root, name);
        public void Dispose() => TestDirectory.Delete(Root);
    }

    private sealed class GitChurnFixture : IDisposable
    {
        public GitChurnFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-churn-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            GitFixture.Initialize(Root);
        }

        public string Root { get; }

        public void Commit(string timestamp, params (string Path, string Content)[] files)
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            Run("add", ".");
            RunWithDate(timestamp, "commit", "--quiet", "-m", timestamp);
        }

        private void Run(params string[] arguments) => RunCore(null, arguments);
        private void RunWithDate(string date, params string[] arguments) => RunCore(date, arguments);

        private void RunCore(string? date, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = Root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            if (date is not null)
            {
                process.StartInfo.Environment["GIT_AUTHOR_DATE"] = date;
                process.StartInfo.Environment["GIT_COMMITTER_DATE"] = date;
            }
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        public void Dispose() => TestDirectory.Delete(Root);
    }
}
