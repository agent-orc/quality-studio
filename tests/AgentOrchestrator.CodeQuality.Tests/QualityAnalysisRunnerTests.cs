using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityAnalysisRunnerTests
{
    [Fact]
    public async Task Run_executes_named_analysis_and_returns_quality_finding_envelopes()
    {
        var directory = Directory.CreateTempSubdirectory("quality-analysis-runner-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Sample.cs"),
                "sealed class Sample { }\n",
                TestContext.Current.CancellationToken);
            var sensor = new RecordingSensor();
            var runner = new QualityAnalysisRunner([sensor]);
            var configuration = new Dictionary<string, string> { ["profile"] = "strict" };

            var result = await runner.RunAsync(new QualityAnalysisRequest(
                directory,
                [new QualityAnalysisDefinition(sensor.Id, configuration)],
                RepositoryId: "sample"), TestContext.Current.CancellationToken);

            var execution = Assert.Single(result.Analyses);
            Assert.Equal(sensor.Id, execution.Name);
            Assert.True(execution.Available);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("sample", Assert.IsType<StandingUnitFindingSubject>(finding.Subject).Repository);
            Assert.Equal(QualityFindingProducerKind.Deterministic, finding.Producer.Kind);
            Assert.Equal("strict", sensor.Request!.Configuration!["profile"]);
            Assert.False(sensor.Request.PersistMetadata);
        }
        finally
        {
            TemporaryDirectory.Delete(directory);
        }
    }

    [Fact]
    public async Task Run_rejects_unknown_or_duplicate_analysis_names_before_execution()
    {
        var directory = Directory.CreateTempSubdirectory("quality-analysis-runner-").FullName;
        try
        {
            var sensor = new RecordingSensor();
            var runner = new QualityAnalysisRunner([sensor]);

            await Assert.ThrowsAsync<SensorNotFoundException>(() => runner.RunAsync(new QualityAnalysisRequest(
                directory,
                [new QualityAnalysisDefinition("missing")]), TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new QualityAnalysisRequest(
                directory,
                [new QualityAnalysisDefinition(sensor.Id), new QualityAnalysisDefinition(sensor.Id)]),
                TestContext.Current.CancellationToken));
            Assert.Null(sensor.Request);
        }
        finally
        {
            TemporaryDirectory.Delete(directory);
        }
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public string Id => "fixture";
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
                "fixture-finding",
                "correctness",
                FindingSeverity.Medium,
                "Fixture finding",
                "The fixture found a concrete issue.",
                "Correct the fixture issue.",
                [new FindingLocation("Sample.cs", new FindingRange(
                    new FindingPosition(1, 1), new FindingPosition(1, 7)))],
                "sha256:" + new string('a', 64),
                "fixture:rule",
                Source: new FindingSource(FindingSourceKind.Deterministic, Id, "fixture-tool", Version));
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "repository", ".", DateTimeOffset.UtcNow.ToString("O"),
                    new Dictionary<string, string> { ["fixture-tool"] = Version })));
        }
    }
}
