namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityAnalysisRunnerTests
{
    [Fact]
    public async Task Runs_selected_analyses_in_order_with_repository_and_configuration()
    {
        var repository = CreateRepository();
        try
        {
            var first = new RecordingAnalysis("first");
            var second = new RecordingAnalysis("second");
            var runner = new QualityAnalysisRunner([first, second]);

            var result = await runner.RunAsync(new QualityAnalysisRequest(
                repository,
                [
                    new QualityAnalysisSelection("second", new Dictionary<string, string>
                    {
                        ["ruleSource"] = "rules/q90.json",
                    }),
                    new QualityAnalysisSelection("first"),
                ],
                PersistMetadata: true), TestContext.Current.CancellationToken);

            Assert.Equal(["first", "second"], runner.AvailableAnalyses);
            Assert.Equal(["second", "first"], result.Analyses.Select(execution => execution.Name));
            Assert.Equal(2, result.Findings.Count);
            Assert.Equal(System.IO.Path.GetFullPath(repository), result.RepositoryPath);
            Assert.Equal("rules/q90.json", second.Request!.Configuration!["ruleSource"]);
            Assert.True(second.Request.PersistMetadata);
        }
        finally
        {
            TestDirectory.Delete(repository);
        }
    }

    [Fact]
    public async Task Rejects_unknown_and_duplicate_analysis_names()
    {
        var repository = CreateRepository();
        try
        {
            var runner = new QualityAnalysisRunner([new RecordingAnalysis("known")]);

            await Assert.ThrowsAsync<SensorNotFoundException>(() => runner.RunAsync(
                new QualityAnalysisRequest(repository, [new QualityAnalysisSelection("missing")]),
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new QualityAnalysisRequest(repository,
                [new QualityAnalysisSelection("known"), new QualityAnalysisSelection("KNOWN")]),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            TestDirectory.Delete(repository);
        }
    }

    private static string CreateRepository()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "quality-analysis-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingAnalysis(string id) : IReviewSensor
    {
        public string Id => id;
        public string Version => "test";
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
                $"{id}-finding",
                "architecture",
                FindingSeverity.Medium,
                $"{id} finding",
                "description",
                "recommendation",
                [new FindingLocation("src/example.cs")],
                $"sha256:{new string(id[0], 64)}",
                $"test:{id}");
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(id, Version, "repository", ".", "2026-08-27T00:00:00Z",
                    new Dictionary<string, string>())));
        }
    }
}
