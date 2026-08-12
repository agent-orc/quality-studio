using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationStoreTests
{
    [Fact]
    public async Task ForcedRunsFromDifferentModelsRemainQueryableBesideOneCurrentSidecar()
    {
        await WithRepositoryAsync(async root =>
        {
            var options = new QualityTaxonomyOptions { ObservationWriteEnabled = true };
            var first = await new ReviewRunner(new RouteAgent("run-a", "model-a"), taxonomyOptions: options)
                .ReviewAsync(Request(root, "model-a"), TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new RouteAgent("run-b", "model-b-requested", "model-b-effective"),
                    taxonomyOptions: options)
                .ReviewAsync(Request(root, "model-b-requested"), TestContext.Current.CancellationToken);

            var stored = (await QualityObservationStore.ReadAllAsync(root, TestContext.Current.CancellationToken))
                .Select(item => Assert.IsType<QualityObservation>(item.Observation)).ToArray();
            Assert.Equal(2, stored.Length);
            Assert.Equal(["model-a", "model-b-effective"], stored.Select(item => item.Producer.EffectiveModel)
                .Order(StringComparer.Ordinal).ToArray());
            var mismatchedRoute = Assert.Single(stored,
                item => item.Producer.RequestedModel != item.Producer.EffectiveModel);
            Assert.Equal("model-b-requested", mismatchedRoute.Producer.RequestedModel);
            Assert.Equal("model-b-effective", mismatchedRoute.Producer.EffectiveModel);
            Assert.All(stored, item =>
            {
                Assert.Equal("openai", item.Producer.Provider);
                Assert.Equal("high", item.Producer.ThinkingLevel);
                Assert.Equal("2026-07-24", item.Producer.RoutePolicyVersion);
                Assert.Equal(item.Producer.RunId, item.Producer.UsageRunId);
                Assert.Equal("review-sweep", item.Producer.ReviewRunId);
                Assert.Equal("inconclusive", item.Assessment);
            });
            Assert.Single(Directory.EnumerateFiles(root, "*.review-meta.code.json", SearchOption.AllDirectories));
            Assert.NotEqual(first.QualityObservation!.ObservationId, second.QualityObservation!.ObservationId);

            var usage = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, usage.Runs);
            Assert.All(stored, item => Assert.Contains(usage.Recent,
                entry => entry.RunId == item.Producer.UsageRunId));

            var schema = QualityTaxonomyTests.ObservationSchema.Value;
            foreach (var line in await File.ReadAllLinesAsync(
                         QualityObservationStore.GetLedgerPath(root, stored[0].ObservedAt),
                         TestContext.Current.CancellationToken))
            {
                using var json = JsonDocument.Parse(line);
                var validation = schema.Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
            }
        });
    }

    [Fact]
    public async Task ReplayIsIdempotentAndMalformedHistoryDoesNotHideLaterObservations()
    {
        await WithRepositoryAsync(async root =>
        {
            var options = new QualityTaxonomyOptions { ObservationWriteEnabled = true };
            var request = Request(root, "model-a");
            var first = await new ReviewRunner(new RouteAgent("stable-run", "model-a"), taxonomyOptions: options)
                .ReviewAsync(request, TestContext.Current.CancellationToken);
            var path = QualityObservationStore.GetLedgerPath(root, first.QualityObservation!.ObservedAt);
            await File.AppendAllTextAsync(path, "{malformed\n", TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new RouteAgent("stable-run", "model-a"), taxonomyOptions: options)
                .ReviewAsync(request, TestContext.Current.CancellationToken);
            var later = first.QualityObservation with
            {
                ObservationId = QualityObservationJson.CreateObservationId("later", "unit", "code", "subject", "input", "taxonomy"),
                ObservedAt = first.QualityObservation.ObservedAt.AddSeconds(1),
            };
            await QualityObservationStore.AppendAsync(root, later, TestContext.Current.CancellationToken);

            var stored = await QualityObservationStore.ReadAllAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(2, stored.Count);
            Assert.True(first.QualityObservationAppended);
            Assert.False(second.QualityObservationAppended);
            Assert.Equal(first.QualityObservation.ObservationId, second.QualityObservation!.ObservationId);
        });
    }

    [Fact]
    public async Task ObservationAppendFailureLeavesPreviousSidecarCurrent()
    {
        await WithRepositoryAsync(async root =>
        {
            var request = Request(root, "model-a");
            var first = await new ReviewRunner(new RouteAgent("run-a", "model-a"))
                .ReviewAsync(request, TestContext.Current.CancellationToken);
            var previous = await File.ReadAllBytesAsync(first.MetaPath, TestContext.Current.CancellationToken);
            var observationsPath = Path.Combine(root, ".quality", "observations");
            Directory.CreateDirectory(Path.GetDirectoryName(observationsPath)!);
            await File.WriteAllTextAsync(observationsPath, "blocks the observation directory",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ReviewRunner(new RouteAgent("run-b", "model-b"),
                        taxonomyOptions: new QualityTaxonomyOptions { ObservationWriteEnabled = true })
                    .ReviewAsync(Request(root, "model-b"), TestContext.Current.CancellationToken));

            Assert.Equal(previous,
                await File.ReadAllBytesAsync(first.MetaPath, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task DisabledDualWriteDoesNotCreateObservationStore()
    {
        await WithRepositoryAsync(async root =>
        {
            var result = await new ReviewRunner(new RouteAgent("run-a", "model-a"))
                .ReviewAsync(Request(root, "model-a"), TestContext.Current.CancellationToken);

            Assert.Null(result.QualityObservation);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality", "observations")));
            Assert.True(File.Exists(result.MetaPath));
        });
    }

    private static ReviewRequest Request(string root, string model) => new(
        "src/Small.cs",
        RepositoryRoot: root,
        ReviewRunId: "review-sweep",
        RequestedModel: model,
        Provider: "openai",
        ThinkingLevel: "high",
        RoutePolicyVersion: "2026-07-24");

    private static async Task WithRepositoryAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root, "src", "Small.cs"),
            "internal static class Small { }\n", TestContext.Current.CancellationToken);
        try
        {
            await test(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class RouteAgent(string runId, string model, string? effectiveModel = null) : IReviewAgent
    {
        public string AgentName => "codex";
        public string? Model => model;
        public string? Provider => "openai";
        public string? ThinkingLevel => "high";
        public string? RoutePolicyVersion => "2026-07-24";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult(
                runId,
                $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
                new TokenUsage(100, 20, 10, 5, 200),
                effectiveModel ?? model,
                Provider,
                ThinkingLevel,
                RoutePolicyVersion: RoutePolicyVersion));
    }
}
