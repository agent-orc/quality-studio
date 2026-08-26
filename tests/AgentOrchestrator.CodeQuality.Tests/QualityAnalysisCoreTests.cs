using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityAnalysisCoreTests
{
    [Fact]
    public async Task Named_analysis_returns_canonical_findings_and_passes_rule_content_as_configuration()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality", "rules"));
            await File.WriteAllTextAsync(Path.Combine(root, "Example.cs"), "class Example {}", TestContext.Current.CancellationToken);
            var rulePath = Path.Combine(root, ".quality", "rules", "security.md");
            await File.WriteAllTextAsync(rulePath, "Require authorization.", TestContext.Current.CancellationToken);
            var sensor = new RecordingSensor();
            var core = new QualityAnalysisCore([sensor]);
            var configuration = new QualityAnalysisConfiguration(
                RepositoryId: "catalog",
                RuleLibraryPath: ".quality/rules",
                Analyses: new Dictionary<string, QualityNamedAnalysisConfiguration>
                {
                    [QualityAnalysisNames.Boundaries] = new(new Dictionary<string, string>
                    {
                        ["profile"] = "strict",
                    }),
                });

            var result = await core.RunAsync(new QualityAnalysisRequest(
                root,
                [QualityAnalysisNames.Boundaries],
                configuration), TestContext.Current.CancellationToken);

            var finding = Assert.Single(result.Findings);
            var subject = Assert.IsType<StandingUnitFindingSubject>(finding.Subject);
            Assert.Equal("catalog", result.RepositoryId);
            Assert.Equal("catalog", subject.Repository);
            Assert.Equal(".", subject.Path);
            Assert.Equal(QualityFindingProducerKind.Deterministic, finding.Producer.Kind);
            Assert.Equal("boundaries", finding.Producer.Id);
            Assert.Equal(".quality/rules", sensor.Request!.Configuration!["ruleLibraryPath"]);
            Assert.Equal("strict", sensor.Request.Configuration["profile"]);
            Assert.False(sensor.Request.PersistMetadata);
            Assert.Single(result.Executions);

            await File.WriteAllTextAsync(rulePath, "Require authorization and audit logging.", TestContext.Current.CancellationToken);
            var changedRules = await core.RunAsync(new QualityAnalysisRequest(
                root,
                [QualityAnalysisNames.Boundaries],
                configuration), TestContext.Current.CancellationToken);
            var changedSubject = Assert.IsType<StandingUnitFindingSubject>(Assert.Single(changedRules.Findings).Subject);
            Assert.NotEqual(subject.ReviewedHash, changedSubject.ReviewedHash);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Unknown_analysis_is_rejected_before_sensor_execution()
    {
        var root = CreateRepository();
        try
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                new QualityAnalysisCore([]).RunAsync(
                    new QualityAnalysisRequest(root, ["not-installed"]),
                    TestContext.Current.CancellationToken));

            Assert.Contains("Available analyses", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Analysis_assembly_does_not_contain_agent_studio_http_hosting()
    {
        var assembly = typeof(QualityAnalysisCore).Assembly;

        Assert.Null(assembly.GetType("AgentOrchestrator.CodeQuality.AgentStudioTaskClient"));
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-analysis-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class RecordingSensor : IReviewSensor
    {
        public SensorScanRequest? Request { get; private set; }

        public string Id => QualityAnalysisNames.Boundaries;

        public string Version => "test-1";

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
                [new ReviewFinding(
                    "boundary-auth",
                    "security",
                    FindingSeverity.High,
                    "Boundary has no proven authorization",
                    "The boundary is reachable without proven authorization.",
                    "Add and test an authorization policy.",
                    [new FindingLocation("Example.cs")],
                    "sha256:" + new string('a', 64),
                    "boundary:authorization")],
                new SensorProvenance(Id, Version, "repository", ".", "2026-08-26T00:00:00Z",
                    new Dictionary<string, string>())));
        }
    }
}
