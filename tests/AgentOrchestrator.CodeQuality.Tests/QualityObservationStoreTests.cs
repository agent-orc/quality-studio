using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationStoreTests
{
    [Fact]
    public async Task EnabledReviewWritesObservationBeforeCurrentSidecarAndLinksUsage()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var agent = new RouteAgent("run-a", "model-effective-a");
            var result = await new ReviewRunner(agent).ReviewAsync(new ReviewRequest(
                "src/Small.cs",
                RepositoryRoot: root,
                ReviewRunId: "review-sweep-a",
                ObservationWriteEnabled: true,
                Provider: "provider-requested",
                RequestedModel: "model-requested-a",
                ThinkingLevel: "high",
                RoutePolicyVersion: "policy-2026-08-11"), TestContext.Current.CancellationToken);

            Assert.True(File.Exists(result.MetaPath));
            var observation = Assert.IsType<QualityObservationDocument>(result.QualityObservation);
            var ledger = await QualityObservationStore.ReadAllAsync(root, TestContext.Current.CancellationToken);
            var stored = Assert.Single(ledger.Observations);
            Assert.Equal(observation.ObservationId, stored.ObservationId);
            Assert.Equal("provider-requested", stored.Producer.Provider);
            Assert.Equal("model-requested-a", stored.Producer.RequestedModel);
            Assert.Equal("model-effective-a", stored.Producer.EffectiveModel);
            Assert.Equal("high", stored.Producer.ThinkingLevel);
            Assert.Equal("policy-2026-08-11", stored.Producer.RoutePolicyVersion);
            Assert.Equal("run-a", stored.Producer.RunId);
            Assert.Equal("review-sweep-a", stored.Producer.ReviewRunId);
            Assert.Equal("not-assessed", stored.Assessment);
            Assert.Equal("code.correctness", Assert.Single(stored.Aspects).AspectId);
            Assert.Equal(0, ledger.MalformedLines);

            var usage = Assert.Single((await UsageLedger.QueryAsync(root,
                cancellationToken: TestContext.Current.CancellationToken)).Recent);
            Assert.Equal(UsageLedger.CurrentSchemaVersion, usage.SchemaVersion);
            Assert.Equal(stored.ObservationId, usage.ObservationId);
            Assert.Equal(stored.Producer.RequestedModel, usage.RequestedModel);
            Assert.Equal(stored.Producer.EffectiveModel, usage.EffectiveModel);

            var usageSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v3.schema.json")));
            var usagePath = UsageLedger.GetLedgerPath(root, usage.Timestamp);
            using var usageJson = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(
                usagePath, TestContext.Current.CancellationToken)));
            var usageValidation = usageSchema.Evaluate(usageJson.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(usageValidation.IsValid, usageValidation.ToString());

            using var json = JsonDocument.Parse(QualityObservationJson.Serialize(stored));
            Assert.Equal(QualityObservationDocument.SchemaId,
                json.RootElement.GetProperty("$schema").GetString());
        });
    }

    [Fact]
    public async Task DifferentModelRunsRemainQueryableWhileSidecarProjectsTheLatest()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var first = await new ReviewRunner(new RouteAgent("run-model-a", "model-a")).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root, ObservationWriteEnabled: true,
                    RequestedModel: "model-a"), TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new RouteAgent("run-model-b", "model-b")).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root, ObservationWriteEnabled: true,
                    RequestedModel: "model-b"), TestContext.Current.CancellationToken);

            var ledger = await QualityObservationStore.ReadAllAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(2, ledger.Observations.Count);
            Assert.Equal(["model-a", "model-b"], ledger.Observations
                .Select(item => item.Producer.EffectiveModel).Order(StringComparer.Ordinal));
            Assert.Equal(first.MetaPath, second.MetaPath);
            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(second.MetaPath,
                TestContext.Current.CancellationToken));
            Assert.Equal("model-b", sidecar.RootElement.GetProperty("reviewer").GetProperty("model").GetString());
        });
    }

    [Fact]
    public async Task ReplayIsIdempotentAndMalformedLinesDoNotHideValidRecords()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var request = new ReviewRequest("src/Small.cs", RepositoryRoot: root,
                ObservationWriteEnabled: true, RequestedModel: "model-a");
            await new ReviewRunner(new RouteAgent("replayed-run", "model-a")).ReviewAsync(
                request, TestContext.Current.CancellationToken);
            await new ReviewRunner(new RouteAgent("replayed-run", "model-a")).ReviewAsync(
                request, TestContext.Current.CancellationToken);

            var path = Directory.EnumerateFiles(Path.Combine(root, ".quality", "observations"), "*.jsonl").Single();
            await File.AppendAllTextAsync(path, "{malformed\n", TestContext.Current.CancellationToken);
            var ledger = await QualityObservationStore.ReadAllAsync(root, TestContext.Current.CancellationToken);

            Assert.Single(ledger.Observations);
            Assert.Equal(1, ledger.MalformedLines);
            Assert.Equal(2, (await UsageLedger.QueryAsync(root,
                cancellationToken: TestContext.Current.CancellationToken)).Runs);
        });
    }

    [Fact]
    public async Task WriteFlagDefaultsOffAndAppendFailureLeavesPriorSidecarCurrent()
    {
        await WithReviewFileAsync(async (root, _) =>
        {
            var first = await new ReviewRunner(new RouteAgent("run-current", "model-current")).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root), TestContext.Current.CancellationToken);
            var priorSidecar = await File.ReadAllBytesAsync(first.MetaPath, TestContext.Current.CancellationToken);
            Assert.Null(first.QualityObservation);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality", "observations")));

            Directory.CreateDirectory(Path.Combine(root, ".quality"));
            await File.WriteAllTextAsync(Path.Combine(root, ".quality", "observations"), "blocks-directory",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                new ReviewRunner(new RouteAgent("run-failed-append", "model-new")).ReviewAsync(
                    new ReviewRequest("src/Small.cs", RepositoryRoot: root, ObservationWriteEnabled: true),
                    TestContext.Current.CancellationToken));

            Assert.Equal(priorSidecar,
                await File.ReadAllBytesAsync(first.MetaPath, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task UnsupportedObservationLinesAreQuarantinedWithRawData()
    {
        var root = Directory.CreateTempSubdirectory("quality-observation-ledger-");
        try
        {
            var directory = Path.Combine(root.FullName, ".quality", "observations");
            Directory.CreateDirectory(directory);
            var unsupportedSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                    "quality-observation.v1.valid.json"))
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 7", StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(unsupportedSource);
            var unsupported = JsonSerializer.Serialize(parsed.RootElement);
            await File.WriteAllTextAsync(Path.Combine(directory, "2026-08.jsonl"), unsupported + "\n",
                TestContext.Current.CancellationToken);

            var ledger = await QualityObservationStore.ReadAllAsync(root.FullName,
                TestContext.Current.CancellationToken);

            Assert.Empty(ledger.Observations);
            var quarantined = Assert.Single(ledger.Unsupported);
            Assert.Equal(7, quarantined.Raw.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task WithReviewFileAsync(Func<string, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var file = Path.Combine(root, "src", "Small.cs");
        await File.WriteAllTextAsync(file, "internal static class Small { }\n", TestContext.Current.CancellationToken);
        try
        {
            await test(root, file);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class RouteAgent(string runId, string effectiveModel) : IReviewAgent
    {
        public string AgentName => "test-agent";
        public string? Model => effectiveModel;

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) => Task.FromResult(new ReviewAgentResult(
            runId,
            $"```json\n{ReviewResponseParserTests.ValidResponse}\n```",
            new TokenUsage(12, 4, 2, 1, 25),
            effectiveModel));
    }
}
