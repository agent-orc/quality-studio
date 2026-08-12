namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityAnalysisCoreTests
{
    [Fact]
    public async Task RunAsync_passes_repository_scope_configuration_and_write_policy_to_named_analysis()
    {
        var root = Directory.CreateTempSubdirectory("quality-analysis-core-").FullName;
        try
        {
            var sensor = new RecordingSensor();
            var core = new QualityAnalysisCore([sensor]);
            var configuration = new Dictionary<string, string> { ["rulesPath"] = "rules/quality.json" };

            var result = await core.RunAsync(new QualityAnalysisRequest(
                root,
                [new NamedQualityAnalysis("sample", configuration, QualityAnalysisScope.Path, "src")],
                PersistArtifacts: false), TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(root), result.RepositoryPath);
            var analysis = Assert.Single(result.Analyses);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("sample", analysis.Name);
            Assert.Equal("sample/rule", finding.RuleId);
            Assert.NotNull(sensor.Request);
            Assert.Equal(Path.GetFullPath(root), sensor.Request.RepositoryRoot);
            Assert.Equal(SensorScope.Path, sensor.Request.Scope);
            Assert.Equal("src", sensor.Request.Path);
            Assert.Same(configuration, sensor.Request.Configuration);
            Assert.False(sensor.Request.PersistMetadata);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CreateDefault_exposes_the_supported_analysis_names_without_running_tools()
    {
        var names = QualityAnalysisCore.CreateDefault().ListAnalyses()
            .Select(analysis => analysis.Name)
            .ToArray();

        Assert.Equal(
        [
            QualityAnalysisNames.Boundaries,
            QualityAnalysisNames.Coverage,
            QualityAnalysisNames.Dependencies,
            QualityAnalysisNames.Eslint,
            QualityAnalysisNames.Gitleaks,
            QualityAnalysisNames.Roslyn,
            QualityAnalysisNames.Sarif,
            QualityAnalysisNames.TypeScript,
        ], names);
    }

    [Fact]
    public async Task RunAsync_reports_unknown_analysis_with_discoverable_names()
    {
        var root = Directory.CreateTempSubdirectory("quality-analysis-core-unknown-").FullName;
        try
        {
            var exception = await Assert.ThrowsAsync<QualityAnalysisNotFoundException>(() =>
                new QualityAnalysisCore([new RecordingSensor()]).RunAsync(new QualityAnalysisRequest(
                    root,
                    [new NamedQualityAnalysis("missing")]), TestContext.Current.CancellationToken));

            Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
            Assert.Contains("sample", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Core_assembly_has_no_AspNetCore_or_Agent_Studio_HTTP_client()
    {
        var assembly = typeof(QualityAnalysisCore).Assembly;

        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
        Assert.Null(assembly.GetType("AgentOrchestrator.CodeQuality.AgentStudioTaskClient"));
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public string Id => "sample";

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
                "sample-finding",
                "sample",
                FindingSeverity.Medium,
                "Sample finding",
                "A sample finding produced by the test analysis.",
                "Address the sample finding.",
                [new FindingLocation("src/Sample.cs")],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "sample/rule");
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "path", "src", "2026-08-12T00:00:00Z",
                    new Dictionary<string, string>())));
        }
    }
}
