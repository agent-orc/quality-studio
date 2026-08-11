using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationStoreTests
{
    private static readonly QualityTaxonomyOptions Enabled = new() { ObservationWriteEnabled = true };

    [Fact]
    public void Cli_flags_load_from_configuration_names_and_documented_aliases()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["QualityTaxonomy__ObservationWriteEnabled"] = "true",
            ["QUALITY_TAXONOMY_OBSERVATION_READ_ENABLED"] = "TRUE",
        };

        var options = QualityTaxonomyOptions.FromEnvironment(name =>
            values.TryGetValue(name, out var value) ? value : null);

        Assert.True(options.ObservationWriteEnabled);
        Assert.True(options.ObservationReadEnabled);
    }

    [Fact]
    public async Task Forced_model_runs_are_immutable_joinable_and_leave_one_current_sidecar()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var first = await new ReviewRunner(new ObservationAgent("run-a", "model-a"),
                    qualityTaxonomyOptions: Enabled)
                .ReviewAsync(Request(root, "model-a"), TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new ObservationAgent("run-b", "model-b"),
                    qualityTaxonomyOptions: Enabled)
                .ReviewAsync(Request(root, "model-b"), TestContext.Current.CancellationToken);

            var stored = await new QualityObservationStore(root).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, stored.Observations.Count);
            Assert.Empty(stored.Unsupported);
            Assert.Equal(0, stored.MalformedLines);
            Assert.Equal(["model-a", "model-b"], stored.Observations
                .OrderBy(item => item.Producer.EffectiveModel, StringComparer.Ordinal)
                .Select(item => item.Producer.EffectiveModel));
            Assert.All(stored.Observations, observation =>
            {
                Assert.Equal("openai", observation.Producer.Provider);
                Assert.Equal("high", observation.Producer.ThinkingLevel);
                Assert.Equal("2026-07-24", observation.Producer.RoutePolicyVersion);
                Assert.Equal("review-sweep", observation.Producer.ReviewRunId);
            });
            var usage = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, usage.Runs);
            Assert.All(stored.Observations, observation =>
                Assert.Contains(usage.Recent, entry => entry.RunId == observation.Producer.RunId));
            Assert.NotEqual(first.ObservationId, second.ObservationId);
            Assert.Equal(first.MetaPath, second.MetaPath);
            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(
                second.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("model-b", sidecar.RootElement.GetProperty("reviewer").GetProperty("model").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Replay_is_idempotent_and_malformed_lines_do_not_hide_valid_observations()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            var runner = new ReviewRunner(new ObservationAgent("stable-run", "model-a"),
                qualityTaxonomyOptions: Enabled);
            var first = await runner.ReviewAsync(Request(root, "model-a"), TestContext.Current.CancellationToken);
            var second = await runner.ReviewAsync(Request(root, "model-a"), TestContext.Current.CancellationToken);
            var store = new QualityObservationStore(root);
            await File.AppendAllTextAsync(store.GetLedgerPath(DateTimeOffset.UtcNow),
                "{\"observationId\":\n", TestContext.Current.CancellationToken);
            var afterPartialLine = QualityTaxonomyContractTests.CreateObservation(new string('f', 64));
            Assert.True(await store.AppendAsync(afterPartialLine, TestContext.Current.CancellationToken));
            await File.AppendAllTextAsync(store.GetLedgerPath(DateTimeOffset.UtcNow),
                "{\"$schema\":\"https://quality.studio/schemas/quality-observation.v1.schema.json\",\"schemaVersion\":1}\n",
                TestContext.Current.CancellationToken);

            var stored = await store.ReadAllAsync(TestContext.Current.CancellationToken);

            Assert.Equal(first.ObservationId, second.ObservationId);
            Assert.Equal(2, stored.Observations.Count);
            Assert.Contains(stored.Observations, item => item.ObservationId == afterPartialLine.ObservationId);
            Assert.Equal(2, stored.MalformedLines);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Observation_precedes_projection_and_survives_projection_failure()
    {
        var root = await CreateRepositoryAsync();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "findings"), "path conflict",
                TestContext.Current.CancellationToken);
            var runner = new ReviewRunner(new ObservationAgent("recoverable-run", "model-a"),
                qualityTaxonomyOptions: Enabled);

            await Assert.ThrowsAnyAsync<IOException>(() => runner.ReviewAsync(
                Request(root, "model-a"), TestContext.Current.CancellationToken));

            var stored = await new QualityObservationStore(root).ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.Equal("recoverable-run", Assert.Single(stored.Observations).Producer.RunId);
            Assert.Empty(Directory.EnumerateFiles(root, "*.review-meta.*.json", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ReviewRequest Request(string root, string model) => new(
        "src/Small.cs",
        RepositoryRoot: root,
        ReviewRunId: "review-sweep",
        Provider: "openai",
        RequestedModel: model,
        ThinkingLevel: "high",
        RoutingPolicyVersion: "2026-07-24");

    private static async Task<string> CreateRepositoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Small.cs"),
            "internal static class Small { }\n", TestContext.Current.CancellationToken);
        return root;
    }

    private sealed class ObservationAgent(string runId, string model) : IReviewAgent
    {
        public string AgentName => "codex";
        public string? Model => model;

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                runId,
                $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(100, 20, 10, 5, 200),
                model,
                "openai",
                "high",
                "2026-07-24"));
    }
}
