using QualityStudio.Analysis;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalysisRunnerTests
{
    [Fact]
    public async Task Runs_named_analyses_and_returns_canonical_findings()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var sensor = new RecordingSensor();
        var runner = new AnalysisRunner([sensor]);

        var result = await runner.RunAsync(new AnalysisRunRequest(
            repository,
            [new NamedAnalysis("rules", new Dictionary<string, string> { ["rulesPath"] = "rules" })],
            RelativePath: "src"), TestContext.Current.CancellationToken);

        var analysis = Assert.Single(result.Analyses);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("rules", analysis.Name);
        Assert.Same(analysis.Findings[0], finding);
        Assert.Equal("qs:test-rule", finding.RuleId);
        Assert.Equal(SensorScope.Path, sensor.Request!.Scope);
        Assert.Equal("src", sensor.Request.Path);
        Assert.Equal("rules", sensor.Request.Configuration!["rulesPath"]);
        Assert.False(sensor.Request.PersistMetadata);
    }

    [Fact]
    public async Task Resolves_all_names_before_starting_any_analysis()
    {
        var repository = RepositoryTestContext.FindRepositoryRoot();
        var sensor = new RecordingSensor();
        var runner = new AnalysisRunner([sensor]);

        await Assert.ThrowsAsync<SensorNotFoundException>(() => runner.RunAsync(new AnalysisRunRequest(
            repository,
            [new NamedAnalysis("rules"), new NamedAnalysis("missing")]),
            TestContext.Current.CancellationToken));

        Assert.Null(sensor.Request);
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public string Id => "rules";
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes => [SensorScope.Repository, SensorScope.Path];
        public SensorScanRequest? Request { get; private set; }

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var finding = new ReviewFinding(
                "finding-1",
                "code",
                FindingSeverity.Medium,
                "Test finding",
                "A rule found a problem.",
                "Fix the problem.",
                [new FindingLocation("src/example.cs")],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "qs:test-rule");
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "path", "src", "2026-08-12T00:00:00Z",
                    new Dictionary<string, string>())));
        }
    }
}
