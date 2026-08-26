using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationStoreTests
{
    [Fact]
    public async Task Forced_model_runs_leave_two_observations_one_sidecar_and_joined_usage()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-dual-write-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "src", "Small.cs"),
                "internal static class Small { }\n",
                TestContext.Current.CancellationToken);
            var options = new QualityTaxonomyOptions { ObservationWriteEnabled = true };

            var first = await new ReviewRunner(
                    new ProvenanceAgent("model-a", "run-a"), taxonomyOptions: options)
                .ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root.FullName),
                    TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(
                    new ProvenanceAgent("model-b", "run-b"), taxonomyOptions: options)
                .ReviewAsync(new ReviewRequest("src/Small.cs", RepositoryRoot: root.FullName),
                    TestContext.Current.CancellationToken);

            var observations = await QualityObservationStore.ReadAsync(
                root.FullName, TestContext.Current.CancellationToken);
            var usage = await UsageLedger.QueryAsync(
                root.FullName, cancellationToken: TestContext.Current.CancellationToken);
            var sidecars = Directory.EnumerateFiles(
                root.FullName, "*.review-meta.code.json", SearchOption.AllDirectories).ToArray();

            Assert.Equal(2, observations.Count);
            Assert.Equal(["model-a", "model-b"],
                observations.Select(item => item.Producer.EffectiveModel).Order(StringComparer.Ordinal));
            Assert.Equal(["run-a", "run-b"],
                observations.Select(item => item.Producer.RunId).Order(StringComparer.Ordinal));
            Assert.Equal(2, usage.Runs);
            Assert.All(observations, observation =>
            {
                Assert.Contains(usage.Recent, entry => entry.RunId == observation.Producer.RunId);
                Assert.Equal("test-provider", observation.Producer.Provider);
                Assert.Equal("high", observation.Producer.ThinkingLevel);
                Assert.Equal("2026-07-24", observation.Producer.RoutePolicyVersion);
            });
            Assert.Single(sidecars);
            Assert.NotEqual(first.QualityObservationId, second.QualityObservationId);
            Assert.NotNull(first.QualityObservationPath);
            Assert.NotNull(second.QualityObservationPath);
            var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(
                    RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"),
                TestContext.Current.CancellationToken),
                new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            foreach (var line in await File.ReadAllLinesAsync(
                         Path.Combine(root.FullName, first.QualityObservationPath!),
                         TestContext.Current.CancellationToken))
            {
                using var json = JsonDocument.Parse(line);
                var validation = schema.Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
            }
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Append_is_idempotent_and_reader_tolerates_malformed_lines()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-idempotence-");
        try
        {
            var observation = CreateObservation("run-one", "model-one");
            var first = await QualityObservationStore.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken);
            var replay = await QualityObservationStore.AppendAsync(
                root.FullName, observation, TestContext.Current.CancellationToken);
            await File.AppendAllTextAsync(first.Path, "{malformed\n", TestContext.Current.CancellationToken);
            var second = await QualityObservationStore.AppendAsync(
                root.FullName, CreateObservation("run-two", "model-two"),
                TestContext.Current.CancellationToken);

            var loaded = await QualityObservationStore.ReadAsync(
                root.FullName, TestContext.Current.CancellationToken);

            Assert.True(first.Appended);
            Assert.False(replay.Appended);
            Assert.True(second.Appended);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(3, (await File.ReadAllLinesAsync(
                first.Path, TestContext.Current.CancellationToken)).Length);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task Observation_append_failure_does_not_replace_the_current_sidecar()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-failure-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "src", "Small.cs"),
                "internal static class Small { }\n",
                TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(root.FullName, ".quality"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, ".quality", "observations"),
                "blocks the observation directory",
                TestContext.Current.CancellationToken);
            var runner = new ReviewRunner(
                new ProvenanceAgent("model-a", "run-a"),
                taxonomyOptions: new QualityTaxonomyOptions { ObservationWriteEnabled = true });

            await Assert.ThrowsAnyAsync<IOException>(() => runner.ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root.FullName),
                TestContext.Current.CancellationToken));

            Assert.Empty(Directory.EnumerateFiles(
                root.FullName, "*.review-meta.code.json", SearchOption.AllDirectories));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static QualityObservationDocument CreateObservation(string runId, string model)
    {
        var hash = "sha256:" + new string('a', 64);
        return new QualityObservationDocument
        {
            ObservationId = QualityObservationJson.CreateObservationId(runId, "unit", "code", hash),
            ObservedAt = new DateTimeOffset(2026, 8, 11, 10, 0, 0, TimeSpan.Zero),
            Taxonomy = new QualityTaxonomyReference(
                QualityTaxonomyDocument.CoreId,
                QualityTaxonomyDocument.CoreVersion,
                QualityTaxonomyCatalogue.Core.Digest,
                []),
            Subject = new QualityObservationSubject("unit", hash),
            Profile = new QualityReviewProfile("file-code-review", "1.0.0", hash, hash),
            Producer = new QualityObservationProducer(
                QualityProducerKind.Agent, "test-agent", "test-provider", model, model,
                "high", "2026-07-24", runId, runId),
            EvidenceStatus = QualityEvidenceStatus.Available,
            Evidence = [],
            Aspects = [],
            Assessment = QualityAssessment.Pass,
            Findings = [],
        };
    }

    private sealed class ProvenanceAgent(string model, string runId) : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => model;
        public string Provider => "test-provider";
        public string? ThinkingLevel => "high";
        public string RoutePolicyVersion => "2026-07-24";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                runId,
                "```json\n" + ReviewResponseParserTests.ValidResponse + "\n```",
                new TokenUsage(100, 20, 10, 5, 1000),
                model,
                Provider,
                model,
                ThinkingLevel,
                RoutePolicyVersion));
    }
}
