using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class PreflightTests
{
    [Fact]
    public async Task Collector_runs_each_configured_sensor_once_and_produces_stable_fact_hashes()
    {
        var sensor = new CountingSensor("fixture", available: true, [Finding("fixture", "machine detail")]);
        var collector = new PreflightCollector(new SensorRegistry([sensor]));
        var subject = PreflightSubject.Create([KeyValuePair.Create("src/Thing.cs", "sha256:source")]);
        var configurations = new[]
        {
            new ReviewSensorConfiguration("fixture", new Dictionary<string, string> { ["threshold"] = "10" }),
            new ReviewSensorConfiguration("FIXTURE", new Dictionary<string, string> { ["threshold"] = "10" }),
        };

        var first = await collector.CollectAsync(
            "review-first", ".", subject, configurations, TestContext.Current.CancellationToken);
        var second = await collector.CollectAsync(
            "review-second", ".", subject, configurations, TestContext.Current.CancellationToken);

        Assert.Equal(2, sensor.RunCount);
        var firstResult = Assert.Single(first.Results);
        var secondResult = Assert.Single(second.Results);
        Assert.Equal(PreflightStatus.Findings, firstResult.Status);
        Assert.Equal(firstResult.ResultHash, secondResult.ResultHash);
        Assert.Equal(first.ResultHash, second.ResultHash);
        Assert.Matches("^sha256:[a-f0-9]{64}$", firstResult.Check.ConfigurationHash);
        Assert.True(firstResult.DurationMs >= 0);
    }

    [Fact]
    public async Task Required_unavailable_check_blocks_while_optional_unavailable_remains_visible()
    {
        var required = new CountingSensor("required", available: false, []);
        var optional = new CountingSensor("optional", available: false, []);
        var snapshot = await new PreflightCollector(new SensorRegistry([required, optional])).CollectAsync(
            "review-blocked",
            ".",
            PreflightSubject.Create([KeyValuePair.Create("src/Thing.cs", "sha256:source")]),
            [
                new ReviewSensorConfiguration("required", Required: true),
                new ReviewSensorConfiguration("optional", Required: false),
            ],
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.BlocksModel);
        Assert.All(snapshot.Results, result => Assert.Equal(PreflightStatus.Unavailable, result.Status));
        Assert.Contains(snapshot.Results, result => result.Check.Required);
        Assert.Contains(snapshot.Results, result => !result.Check.Required);
        var optionalOnly = snapshot.Results.Where(result => !result.Check.Required).ToArray();
        Assert.False((snapshot with { Results = optionalOnly }).BlocksModel);
        Assert.Equal(SecurityEvidenceVerdict.Pass, SecurityEvidenceCollector.FromPreflight(
            optionalOnly,
            ["src/Thing.cs"],
            [new ReviewSensorConfiguration("optional", Required: false)]).Verdict);
    }

    [Fact]
    public async Task Subject_or_configuration_change_invalidates_the_result_hash()
    {
        var sensor = new CountingSensor("fixture", available: true, []);
        var collector = new PreflightCollector(new SensorRegistry([sensor]));
        var first = await collector.CollectAsync(
            "review-first",
            ".",
            PreflightSubject.Create([KeyValuePair.Create("src/Thing.cs", "sha256:one")]),
            [new ReviewSensorConfiguration("fixture", new Dictionary<string, string> { ["limit"] = "10" })],
            TestContext.Current.CancellationToken);
        var sourceChanged = await collector.CollectAsync(
            "review-second",
            ".",
            PreflightSubject.Create([KeyValuePair.Create("src/Thing.cs", "sha256:two")]),
            [new ReviewSensorConfiguration("fixture", new Dictionary<string, string> { ["limit"] = "10" })],
            TestContext.Current.CancellationToken);
        var configurationChanged = await collector.CollectAsync(
            "review-third",
            ".",
            first.Subject,
            [new ReviewSensorConfiguration("fixture", new Dictionary<string, string> { ["limit"] = "11" })],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(first.ResultHash, sourceChanged.ResultHash);
        Assert.NotEqual(first.ResultHash, configurationChanged.ResultHash);
    }

    [Fact]
    public async Task Prompt_projection_is_bounded_and_never_includes_secret_or_advisory_prose()
    {
        const string secret = "planted-secret-value-must-not-enter-the-prompt";
        var sensor = new CountingSensor("gitleaks", available: true,
            Enumerable.Range(0, 30).Select(index => Finding($"secret-{index}", secret)).ToArray());
        var snapshot = await new PreflightCollector(new SensorRegistry([sensor])).CollectAsync(
            "review-secret",
            ".",
            PreflightSubject.Create([KeyValuePair.Create("src/Thing.cs", "sha256:source")]),
            [new ReviewSensorConfiguration("gitleaks")],
            TestContext.Current.CancellationToken);

        var deterministicPrompt = PreflightProjection.ToPromptJson(snapshot.Results);
        var securityPrompt = SecurityEvidenceCollector.FromPreflight(
            snapshot.Results,
            ["src/Thing.cs"],
            [new ReviewSensorConfiguration("gitleaks")]).ToPromptJson();

        Assert.True(deterministicPrompt.Length <= PreflightProjection.PromptCharacterLimit);
        Assert.True(securityPrompt.Length <= PreflightProjection.PromptCharacterLimit);
        Assert.DoesNotContain(secret, deterministicPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, securityPrompt, StringComparison.Ordinal);
        Assert.Contains("resultHash", securityPrompt, StringComparison.Ordinal);
        Assert.Contains("findingCount", securityPrompt, StringComparison.Ordinal);
    }

    private static ReviewFinding Finding(string id, string description) => new(
        id,
        "secrets",
        FindingSeverity.High,
        "Machine finding",
        description,
        "Remove it.",
        [new FindingLocation("src/Thing.cs", new FindingRange(
            new FindingPosition(1, 1), new FindingPosition(1, 4)))],
        "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(id))),
        "fixture-rule",
        Evidence: JsonSerializer.Serialize(new { value = description }));

    private sealed class CountingSensor(
        string id,
        bool available,
        IReadOnlyList<ReviewFinding> findings) : IReviewSensor
    {
        private int runCount;

        public int RunCount => runCount;
        public string Id => id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(available, available ? null : "fixture unavailable"));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref runCount);
            return Task.FromResult(new SensorScanResult(
                available,
                available ? null : "fixture unavailable",
                findings,
                new SensorProvenance(
                    Id,
                    Version,
                    "repository",
                    ".",
                    DateTimeOffset.UtcNow.ToString("O"),
                    new Dictionary<string, string> { [Id] = Version })));
        }
    }
}
