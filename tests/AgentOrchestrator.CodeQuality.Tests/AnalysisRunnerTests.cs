namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalysisRunnerTests
{
    [Fact]
    public async Task RunAsync_runs_named_analysis_and_returns_quality_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-analysis-runner-").FullName;
        try
        {
            var sensor = new RecordingSensor();
            var runner = new AnalysisRunner([sensor], new FakeRuleProvider());
            var result = await runner.RunAsync(new AnalysisRequest(
                root,
                ["sample"],
                new AnalysisConfiguration
                {
                    Settings = new Dictionary<string, IReadOnlyDictionary<string, string>>
                    {
                        ["SAMPLE"] = new Dictionary<string, string> { ["threshold"] = "high" },
                    },
                }), TestContext.Current.CancellationToken);

            var execution = Assert.Single(result.Executions);
            var finding = Assert.Single(result.Findings);
            Assert.Equal(Path.GetFullPath(root), result.RepositoryPath);
            Assert.True(result.Available);
            Assert.Equal("sample", execution.Name);
            Assert.Equal("qs-90", execution.RuleSetId);
            Assert.Equal("2026.08", execution.RuleSetVersion);
            Assert.Equal("sample/rule", finding.RuleId);
            Assert.Equal("high", sensor.Request!.Configuration!["threshold"]);
            Assert.Equal("rule-value", sensor.Request.Configuration["from-rules"]);
            Assert.False(sensor.Request.PersistMetadata);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ListAnalyses_exposes_the_default_in_process_catalogue()
    {
        var names = new AnalysisRunner().ListAnalyses().Select(analysis => analysis.Name).ToArray();

        Assert.Contains(AnalysisNames.Boundaries, names);
        Assert.Contains(AnalysisNames.Dependencies, names);
        Assert.Contains(AnalysisNames.Gitleaks, names);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task RunAsync_rejects_duplicate_or_missing_analysis_names()
    {
        var root = Directory.CreateTempSubdirectory("quality-analysis-validation-").FullName;
        try
        {
            var runner = new AnalysisRunner([new RecordingSensor()]);
            await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new AnalysisRequest(root, ["sample", "SAMPLE"]), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SensorNotFoundException>(() => runner.RunAsync(
                new AnalysisRequest(root, ["unknown"]), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeRuleProvider : IAnalysisRuleProvider
    {
        public ValueTask<AnalysisRuleSet?> GetRulesAsync(
            string analysisName,
            string repositoryPath,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AnalysisRuleSet?>(new AnalysisRuleSet(
                "qs-90",
                "2026.08",
                new Dictionary<string, string>
                {
                    ["threshold"] = "low",
                    ["from-rules"] = "rule-value",
                }));
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public string Id => "sample";

        public string Version => "1.2.3";

        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public SensorScanRequest? Request { get; private set; }

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var finding = new ReviewFinding(
                "sample-finding",
                "maintainability",
                FindingSeverity.Medium,
                "Sample finding",
                "A test finding.",
                "Address the test finding.",
                [new FindingLocation("src/Sample.cs")],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sample/rule");
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "repository", ".", "2026-08-26T00:00:00Z",
                    new Dictionary<string, string>())));
        }
    }
}
