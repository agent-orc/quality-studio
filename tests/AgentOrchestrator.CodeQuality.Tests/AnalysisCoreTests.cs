namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalysisCoreTests
{
    [Fact]
    public void CreateDefault_registers_the_built_in_deterministic_sensors()
    {
        var core = AnalysisCore.CreateDefault();

        Assert.Equal(
            new[] { "boundaries", "coverage", "dependencies", "eslint", "gitleaks", "roslyn", "tsc" },
            core.AnalysisIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunAsync_by_id_dispatches_to_the_matching_analysis_and_forwards_the_repository_root()
    {
        var core = new AnalysisCore([new StubSensor("demo")]);

        var result = await core.RunAsync(
            "demo", "/repo",
            configuration: new Dictionary<string, string> { ["x"] = "1" },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Available);
        Assert.Single(result.Findings);
        Assert.Equal("/repo", result.Findings[0].Title);
    }

    [Fact]
    public async Task RunAsync_by_id_throws_for_an_unknown_analysis()
    {
        var core = new AnalysisCore([new StubSensor("demo")]);

        await Assert.ThrowsAsync<SensorNotFoundException>(
            () => core.RunAsync("missing", "/repo", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_without_ids_runs_every_registered_analysis_and_keys_results_by_id()
    {
        var core = new AnalysisCore([new StubSensor("one"), new StubSensor("two")]);

        var results = await core.RunAsync("/repo", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["one", "two"], results.Keys.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunAsync_with_ids_runs_only_the_requested_analyses()
    {
        var core = new AnalysisCore([new StubSensor("one"), new StubSensor("two")]);

        var results = await core.RunAsync(
            "/repo", analysisIds: ["two"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["two"], results.Keys);
    }

    private sealed class StubSensor(string id) : IReviewSensor
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes => [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorScanResult(
                true,
                null,
                [new ReviewFinding(Id, "demo", FindingSeverity.Info, request.RepositoryRoot, "stub", "stub", [], "fp", Id)],
                new SensorProvenance(Id, Version, request.Scope.ToString(), request.RepositoryRoot, "now", new Dictionary<string, string>())));
    }
}
