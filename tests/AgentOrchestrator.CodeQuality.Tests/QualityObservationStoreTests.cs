using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationStoreTests
{
    private static readonly Lazy<JsonSchema> UsageV3Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(),
        "schemas",
        "usage-ledger.v3.schema.json"))));

    [Fact]
    public async Task ForcedModelRunsAppendDistinctObservationsAndKeepOneCurrentSidecar()
    {
        var (root, _) = await CreateRepositoryAsync();
        try
        {
            var first = await RunAsync(root, "run-model-a", "model-a", "review-a");
            var second = await RunAsync(root, "run-model-b", "model-b", "review-b");
            var ledger = new QualityObservationLedger();
            var observations = (await ledger.ReadAsync(root, TestContext.Current.CancellationToken))
                .Select(result => Assert.IsType<QualityObservationDocument>(result.Observation))
                .ToArray();
            var usage = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            using var currentSidecar = JsonDocument.Parse(await File.ReadAllTextAsync(
                second.MetaPath,
                TestContext.Current.CancellationToken));

            Assert.Equal(2, observations.Length);
            Assert.Equal(2, observations.Select(observation => observation.ObservationId).Distinct().Count());
            Assert.Contains(observations, observation => observation.Producer.EffectiveModel == "model-a");
            Assert.Contains(observations, observation => observation.Producer.EffectiveModel == "model-b");
            Assert.Equal("model-b", currentSidecar.RootElement.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.Equal(first.MetaPath, second.MetaPath);
            Assert.Equal(2, usage.Runs);
            foreach (var observation in observations)
            {
                var linkedUsage = Assert.Single(usage.Recent, entry => entry.RunId == observation.Producer.RunId);
                Assert.Equal(3, linkedUsage.SchemaVersion);
                Assert.Equal(observation.Producer.Provider, linkedUsage.Provider);
                Assert.Equal(observation.Producer.RequestedModel, linkedUsage.RequestedModel);
                Assert.Equal(observation.Producer.EffectiveModel, linkedUsage.EffectiveModel);
                Assert.Equal(observation.Producer.ThinkingLevel, linkedUsage.ThinkingLevel);
                Assert.Equal(observation.Producer.RoutePolicyVersion, linkedUsage.RoutePolicyVersion);
                ValidateUsageV3(linkedUsage);
            }

            Assert.False(await ledger.AppendAsync(root, first.QualityObservation!, TestContext.Current.CancellationToken));
            Assert.Equal(2, (await File.ReadAllLinesAsync(
                QualityObservationLedger.GetLedgerPath(root, first.QualityObservation!.ObservedAt),
                TestContext.Current.CancellationToken)).Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LedgerToleratesMalformedLinesWithoutHidingLaterObservations()
    {
        var (root, _) = await CreateRepositoryAsync();
        try
        {
            var first = await RunAsync(root, "run-a", "model-a", "review-a");
            var path = QualityObservationLedger.GetLedgerPath(root, first.QualityObservation!.ObservedAt);
            await File.AppendAllTextAsync(path, "{\"partial\":\n", TestContext.Current.CancellationToken);
            await RunAsync(root, "run-b", "model-b", "review-b");

            var loaded = await new QualityObservationLedger().ReadAsync(root, TestContext.Current.CancellationToken);

            Assert.Equal(2, loaded.Count);
            Assert.All(loaded, result => Assert.Equal(QualityObservationReadStatus.Supported, result.Status));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ObservationAppendFailureLeavesPriorSidecarCurrent()
    {
        var (root, _) = await CreateRepositoryAsync();
        try
        {
            var existing = await RunAsync(root, "run-existing", "model-existing", "review-existing");
            var before = await File.ReadAllTextAsync(existing.MetaPath, TestContext.Current.CancellationToken);
            var runner = new ReviewRunner(
                new RouteAgent("run-failed", "model-new"),
                observationWriter: new ThrowingObservationWriter());

            await Assert.ThrowsAsync<IOException>(() => runner.ReviewAsync(
                Request(root, "model-new", "review-new"),
                TestContext.Current.CancellationToken));

            Assert.Equal(before, await File.ReadAllTextAsync(existing.MetaPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CrashAfterAppendLeavesRecoverableObservationBeforeProjection()
    {
        var (root, _) = await CreateRepositoryAsync();
        try
        {
            var ledger = new QualityObservationLedger();
            var runner = new ReviewRunner(
                new RouteAgent("run-crash", "model-crash"),
                observationWriter: new AppendThenThrowWriter(ledger));

            await Assert.ThrowsAsync<IOException>(() => runner.ReviewAsync(
                Request(root, "model-crash", "review-crash"),
                TestContext.Current.CancellationToken));

            Assert.False(Directory.Exists(Path.Combine(root, "src", ".quality", "reviews")));
            var recovered = Assert.Single(await ledger.ReadAsync(root, TestContext.Current.CancellationToken));
            Assert.Equal("run-crash", Assert.IsType<QualityObservationDocument>(recovered.Observation).Producer.RunId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<ReviewResult> RunAsync(
        string root,
        string runId,
        string model,
        string reviewRunId) =>
        await new ReviewRunner(new RouteAgent(runId, model)).ReviewAsync(
            Request(root, model, reviewRunId),
            TestContext.Current.CancellationToken);

    private static ReviewRequest Request(string root, string model, string reviewRunId) => new(
        "src/Small.cs",
        RepositoryRoot: root,
        ReviewRunId: reviewRunId,
        Provider: "openai",
        RequestedModel: model,
        ThinkingLevel: "high",
        RoutePolicyVersion: "2026-07-24",
        ObservationWriteEnabled: true);

    private static async Task<(string Root, string File)> CreateRepositoryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "src");
        Directory.CreateDirectory(sourceDirectory);
        var file = Path.Combine(sourceDirectory, "Small.cs");
        await File.WriteAllTextAsync(file, "public sealed class Small { }\n", TestContext.Current.CancellationToken);
        return (root, file);
    }

    private static void ValidateUsageV3(ReviewUsageEntry entry)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var result = UsageV3Schema.Value.Evaluate(
            json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private sealed class RouteAgent(string runId, string model) : IReviewAgent
    {
        public string AgentName => "codex";
        public string? Model => model;
        public string? Provider => "openai";
        public string? ThinkingLevel => "high";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                runId,
                ReviewResponseParserTests.ValidResponse,
                new TokenUsage(100, 20, 10, 5, 250),
                model,
                Provider,
                ThinkingLevel));
    }

    private sealed class ThrowingObservationWriter : IQualityObservationWriter
    {
        public Task<bool> AppendAsync(
            string repositoryRoot,
            QualityObservationDocument observation,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new IOException("Observation append failed."));
    }

    private sealed class AppendThenThrowWriter(QualityObservationLedger ledger) : IQualityObservationWriter
    {
        public async Task<bool> AppendAsync(
            string repositoryRoot,
            QualityObservationDocument observation,
            CancellationToken cancellationToken = default)
        {
            await ledger.AppendAsync(repositoryRoot, observation, cancellationToken);
            throw new IOException("Simulated crash after durable append.");
        }
    }
}
