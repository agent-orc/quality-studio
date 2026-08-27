using AgentOrchestrator.CodeQuality;
using QualityStudio.Analysis;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class AnalysisCoreApiAdapterTests
{
    [Fact]
    public async Task Named_sensor_execution_uses_core_facade_and_preserves_api_contract()
    {
        var directory = Directory.CreateTempSubdirectory("quality-api-analysis-core-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Sample.cs"),
                "sealed class Sample { }\n",
                TestContext.Current.CancellationToken);
            var sensor = new RecordingSensor();
            var adapter = new AnalysisCoreApiAdapter(
                new QualityAnalysisRunner([sensor]),
                new SensorRegistry([sensor]));
            var repository = new RepositoryRegistration(
                "sample",
                "Sample",
                directory,
                null,
                12_000,
                ["code"],
                [new RepositorySensorConfiguration(sensor.Id)]);
            var configuration = new RepositorySensorConfiguration(
                sensor.Id,
                Configuration: new Dictionary<string, string> { ["profile"] = "strict" });

            var result = await adapter.RunSensorAsync(
                repository,
                configuration,
                SensorScope.Path,
                "Sample.cs",
                persistMetadata: true,
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal(sensor.Id, result.Provenance.SensorId);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("fixture:rule", finding.RuleId);
            Assert.Equal(sensor.Id, finding.Source!.SensorId);
            Assert.Equal("fixture-tool", finding.Source.Producer);
            Assert.Equal("strict", sensor.Request!.Configuration!["profile"]);
            Assert.Equal(SensorScope.Path, sensor.Request.Scope);
            Assert.Equal("Sample.cs", sensor.Request.Path);
            Assert.True(sensor.Request.PersistMetadata);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Deterministic_evidence_collection_uses_core_facade_without_writing_metadata()
    {
        var directory = Directory.CreateTempSubdirectory("quality-api-analysis-evidence-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "Sample.cs"),
                "sealed class Sample { }\n",
                TestContext.Current.CancellationToken);
            var sensor = new RecordingSensor();
            var adapter = new AnalysisCoreApiAdapter(
                new QualityAnalysisRunner([sensor]),
                new SensorRegistry([sensor]));
            var repository = new RepositoryRegistration(
                "sample",
                "Sample",
                directory,
                null,
                12_000,
                ["code"],
                [new RepositorySensorConfiguration(sensor.Id)]);

            var results = await adapter.CollectDeterministicEvidenceAsync(
                repository,
                repository.Sensors!,
                TestContext.Current.CancellationToken);

            Assert.Single(results);
            Assert.NotNull(sensor.Request);
            Assert.False(sensor.Request.PersistMetadata);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingSensor : IDeterministicEvidenceSensor
    {
        public string Id => "fixture";
        public string Version => "1.2.3";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } =
            [SensorScope.Repository, SensorScope.Path];
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
                [new FindingLocation("Sample.cs")],
                "sha256:" + new string('a', 64),
                "fixture:rule",
                Source: new FindingSource(
                    FindingSourceKind.Deterministic,
                    Id,
                    "fixture-tool",
                    Version));
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(
                    Id,
                    Version,
                    request.Scope.ToString().ToLowerInvariant(),
                    request.Path ?? ".",
                    DateTimeOffset.UtcNow.ToString("O"),
                    new Dictionary<string, string> { ["fixture-tool"] = Version })));
        }
    }
}
