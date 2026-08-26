namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalysisRunnerTests
{
    [Fact]
    public async Task RunAsync_runs_named_analysis_in_process_and_returns_quality_findings()
    {
        using var directory = new TemporaryDirectory();
        var sensor = new RecordingSensor();
        var runner = new AnalysisRunner([sensor]);

        var result = await runner.RunAsync(new AnalysisRequest(
            directory.Path,
            [new AnalysisConfiguration("fixture", new Dictionary<string, string> { ["ruleSet"] = "strict" })]),
            TestContext.Current.CancellationToken);

        var analysis = Assert.Single(result.Analyses);
        Assert.True(analysis.Available);
        Assert.Equal("fixture", analysis.Name);
        Assert.Equal("strict", sensor.Request!.Configuration!["ruleSet"]);
        Assert.False(sensor.Request.PersistMetadata);
        Assert.Equal(Path.GetFullPath(directory.Path), result.RepositoryPath);
        Assert.Same(Assert.Single(analysis.Findings), Assert.Single(result.Findings));
    }

    [Fact]
    public void ListAnalyses_does_not_probe_tools()
    {
        var sensor = new RecordingSensor();

        var descriptor = Assert.Single(new AnalysisRunner([sensor]).ListAnalyses());

        Assert.Equal("fixture", descriptor.Name);
        Assert.Equal("1.2.3", descriptor.Version);
        Assert.False(sensor.Probed);
    }

    [Fact]
    public void Default_runner_exposes_stable_builtin_names_without_web_dependencies()
    {
        var names = new AnalysisRunner().ListAnalyses()
            .Select(analysis => analysis.Name)
            .ToArray();

        Assert.Equal(
        [
            AnalysisNames.Boundaries,
            AnalysisNames.Coverage,
            AnalysisNames.Dependencies,
            AnalysisNames.Eslint,
            AnalysisNames.Gitleaks,
            AnalysisNames.Roslyn,
            AnalysisNames.Sarif,
            AnalysisNames.TypeScript,
        ], names);
        var assembly = typeof(AnalysisRunner).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
        Assert.Null(assembly.GetType("AgentOrchestrator.CodeQuality.AgentStudioTaskClient"));
    }

    [Fact]
    public async Task RunAsync_rejects_duplicate_analysis_names()
    {
        using var directory = new TemporaryDirectory();
        var runner = new AnalysisRunner([new RecordingSensor()]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
            new AnalysisRequest(
                directory.Path,
                [new AnalysisConfiguration("fixture"), new AnalysisConfiguration("FIXTURE")]),
            TestContext.Current.CancellationToken));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public string Id => "fixture";
        public string Version => "1.2.3";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];
        public bool Probed { get; private set; }
        public SensorScanRequest? Request { get; private set; }

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            Probed = true;
            return Task.FromResult(new SensorAvailability(true));
        }

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var finding = new ReviewFinding(
                "fixture-finding",
                "correctness",
                FindingSeverity.High,
                "Fixture finding",
                "The fixture analysis found a problem.",
                "Correct the fixture.",
                [new FindingLocation("src/example.cs")],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "fixture/rule");
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "repository", ".", "2026-08-26T00:00:00Z", new Dictionary<string, string>())));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "quality-analysis-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => TestDirectory.Delete(Path);
    }
}
