using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationLedgerTests
{
    [Fact]
    public async Task DifferentModelRunsRemainImmutableAndJoinV3UsageWhileSidecarProjectsCurrent()
    {
        await WithReviewFileAsync(async root =>
        {
            var first = await new ReviewRunner(new RouteAgent("run-model-a", "model-a")).ReviewAsync(
                Request(root, "model-a"), TestContext.Current.CancellationToken);
            var second = await new ReviewRunner(new RouteAgent("run-model-b", "model-b")).ReviewAsync(
                Request(root, "model-b"), TestContext.Current.CancellationToken);

            var observations = await QualityObservationLedger.ReadAsync(
                root, TestContext.Current.CancellationToken);
            Assert.Equal(2, observations.Count);
            Assert.Equal(["model-a", "model-b"], observations.Select(item => item.Producer.EffectiveModel));
            Assert.All(observations, item =>
            {
                Assert.Equal("openai", item.Producer.Provider);
                Assert.Equal("high", item.Producer.ThinkingLevel);
                Assert.Equal("2026-07-24", item.Producer.RoutePolicyVersion);
                Assert.Equal("code.correctness", Assert.Single(item.Aspects).AspectId);
                var finding = Assert.Single(item.Findings);
                Assert.Equal(FindingIdentity.OccurrenceCanonicalization, finding.FingerprintAlgorithm);
                Assert.Single(finding.FingerprintAliases!);
                Assert.All(finding.EvidenceRefs, reference =>
                    Assert.Contains(item.Evidence, evidence => evidence.Id == reference));
                Assert.True(item.Extensions.ContainsKey(QualityObservationReducer.ProjectionExtension));
            });

            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(
                second.MetaPath, TestContext.Current.CancellationToken));
            Assert.Equal("model-b", sidecar.RootElement.GetProperty("reviewer").GetProperty("model").GetString());
            Assert.Equal(first.MetaPath, second.MetaPath);

            var usage = await UsageLedger.QueryAsync(root, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, usage.Runs);
            Assert.All(usage.Recent, entry =>
            {
                Assert.Equal(3, entry.SchemaVersion);
                Assert.Equal("review-test", entry.ReviewRunId);
                Assert.Contains(observations, item => item.ObservationId == entry.ObservationId);
                Assert.Equal(entry.Model, entry.EffectiveModel);
            });

            var usageSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v3.schema.json")));
            foreach (var line in await File.ReadAllLinesAsync(
                         UsageLedger.GetLedgerPath(root, usage.Recent[0].Timestamp),
                         TestContext.Current.CancellationToken))
            {
                using var lineJson = JsonDocument.Parse(line);
                var validation = usageSchema.Evaluate(
                    lineJson.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
            }
        });
    }

    [Fact]
    public async Task ReplayIsIdempotentAndMalformedLinesDoNotHideLaterObservations()
    {
        await WithReviewFileAsync(async root =>
        {
            var request = Request(root, "model-a");
            await new ReviewRunner(new RouteAgent("stable-run", "model-a")).ReviewAsync(
                request, TestContext.Current.CancellationToken);
            await new ReviewRunner(new RouteAgent("stable-run", "model-a")).ReviewAsync(
                request, TestContext.Current.CancellationToken);

            var path = Directory.EnumerateFiles(
                Path.Combine(root, ".quality", "observations"), "*.jsonl").Single();
            Assert.Single(await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken));

            await File.AppendAllTextAsync(path, "{malformed\n", TestContext.Current.CancellationToken);
            await new ReviewRunner(new RouteAgent("later-run", "model-b")).ReviewAsync(
                Request(root, "model-b"), TestContext.Current.CancellationToken);

            var observations = await QualityObservationLedger.ReadAsync(
                root, TestContext.Current.CancellationToken);
            Assert.Equal(2, observations.Count);
            Assert.Contains(observations, item => item.Producer.EffectiveModel == "model-b");
        });
    }

    [Fact]
    public async Task ProjectionFailureLeavesRecoverableObservationAndPriorSidecarUntouched()
    {
        await WithReviewFileAsync(async root =>
        {
            var initial = await new ReviewRunner(new RouteAgent("initial-run", "model-a")).ReviewAsync(
                new ReviewRequest("src/Small.cs", RepositoryRoot: root),
                TestContext.Current.CancellationToken);
            var priorSidecar = await File.ReadAllTextAsync(initial.MetaPath, TestContext.Current.CancellationToken);
            var runner = new ReviewRunner(
                new RouteAgent("recoverable-run", "model-b"),
                observationWritten: _ => throw new IOException("simulated projection crash"));

            var exception = await Assert.ThrowsAsync<IOException>(() => runner.ReviewAsync(
                Request(root, "model-b"), TestContext.Current.CancellationToken));

            Assert.Contains("projection crash", exception.Message, StringComparison.Ordinal);
            Assert.Equal(priorSidecar,
                await File.ReadAllTextAsync(initial.MetaPath, TestContext.Current.CancellationToken));
            var observation = Assert.Single(await QualityObservationLedger.ReadAsync(
                root, TestContext.Current.CancellationToken));
            Assert.Equal("recoverable-run", observation.Producer.RunId);
            Assert.Equal("model-b", observation.Producer.EffectiveModel);
        });
    }

    private static ReviewRequest Request(string root, string model) => new(
        "src/Small.cs",
        RepositoryRoot: root,
        ReviewRunId: "review-test",
        Provider: "openai",
        RequestedModel: model,
        ThinkingLevel: "high",
        RoutePolicyVersion: "2026-07-24",
        ObservationWriteEnabled: true,
        ObservationReadEnabled: true);

    private static async Task WithReviewFileAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-observation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "src", "Small.cs"),
            "internal static class Small { }\n",
            TestContext.Current.CancellationToken);
        try
        {
            await test(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RouteAgent(string runId, string model) : IReviewAgent
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
                ReviewResponseParserTests.ValidResponse.Replace(
                    "\"findings\": []",
                    "\"findings\": [" + ReviewResponseParserTests.ValidFinding + "]",
                    StringComparison.Ordinal),
                new TokenUsage(120, 34, 56, 7, 890),
                model,
                "openai",
                "high"));
    }
}
