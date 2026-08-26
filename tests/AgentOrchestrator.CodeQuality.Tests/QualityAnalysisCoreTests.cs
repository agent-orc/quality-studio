namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityAnalysisCoreTests
{
    [Fact]
    public async Task RunAsync_executes_named_analyses_and_returns_deduplicated_qs_findings()
    {
        var repository = Directory.CreateTempSubdirectory("quality-analysis-core-").FullName;
        try
        {
            var finding = Finding("shared-fingerprint", "rules:one");
            var first = new StubAnalysis("first", finding);
            var second = new StubAnalysis("second", finding, Finding("unique-fingerprint", "rules:two"));
            var core = new QualityAnalysisCore([first, second]);
            var configuration = new Dictionary<string, string> { ["rulesPath"] = ".quality/rules" };

            var result = await core.RunAsync(new QualityAnalysisRequest(
                repository,
                [new NamedAnalysis("first", configuration), new NamedAnalysis("second")]),
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(repository), result.RepositoryPath);
            Assert.Equal(2, result.Analyses.Count);
            Assert.Equal(2, result.Findings.Count);
            Assert.Equal(".quality/rules", first.Request!.Configuration!["rulesPath"]);
            Assert.False(first.Request.PersistMetadata);
            Assert.Equal(["first", "second"], core.ListAnalyses().Select(item => item.Name));
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    [Fact]
    public void Default_package_surface_has_first_party_analyses_and_no_aspnet_dependency()
    {
        var names = QualityAnalysisCore.CreateDefault().ListAnalyses().Select(item => item.Name).ToArray();

        Assert.Equal(
            ["boundaries", "coverage", "dependencies", "eslint", "gitleaks", "roslyn", "sarif", "tsc"],
            names);
        Assert.DoesNotContain(
            typeof(QualityAnalysisCore).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RunAsync_rejects_an_unknown_analysis_before_any_http_transport_is_needed()
    {
        var repository = Directory.CreateTempSubdirectory("quality-analysis-core-").FullName;
        try
        {
            var core = new QualityAnalysisCore([new StubAnalysis("known")]);

            var exception = await Assert.ThrowsAsync<SensorNotFoundException>(() => core.RunAsync(
                new QualityAnalysisRequest(repository, [new NamedAnalysis("unknown")]),
                TestContext.Current.CancellationToken));

            Assert.Contains("unknown", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repository, true);
        }
    }

    private static ReviewFinding Finding(string fingerprintSeed, string ruleId) => new(
        "finding-" + fingerprintSeed,
        "maintainability",
        FindingSeverity.Medium,
        "A finding",
        "A deterministic analysis found an issue.",
        "Address the issue.",
        [new FindingLocation("src/Example.cs")],
        "sha256:" + new string(fingerprintSeed == "shared-fingerprint" ? 'a' : 'b', 64),
        ruleId,
        Source: new FindingSource(FindingSourceKind.Deterministic, "stub", "stub", "1.0.0"));

    private sealed class StubAnalysis(string id, params ReviewFinding[] findings) : IReviewSensor
    {
        public SensorScanRequest? Request { get; private set; }
        public string Id => id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                findings,
                new SensorProvenance(id, Version, "repository", ".", "2026-08-26T00:00:00Z", new Dictionary<string, string>())));
        }
    }
}
