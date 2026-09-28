using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class UsageLedgerTests
{
    // JsonSchema.Net registers each schema's $id globally and refuses a second registration, so
    // the v3 schema is parsed once for every validation in this class.
    private static readonly Lazy<JsonSchema> LedgerV3Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v3.schema.json"))));

    [Fact]
    public async Task V3EntriesAttributeEveryOperationToAModelSourceAndCarryTheirCost()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-v3-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-3", timestamp, "gpt-5.6-luna", "codex",
                new TokenUsage(50, 10, 20, 2, 600), "code", "file", "src/c.ts",
                "review-sweep-3", 3, ReviewModelSource.PolicyDefault,
                new UsageCost(0.5m, "USD", "resolved")),
                TestContext.Current.CancellationToken);
            // A standalone CLI review has no sweep id but still names its model source; an entry
            // without a stored cost is priced at query time.
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-4", timestamp.AddMinutes(1), "gpt-5.6-sol", "codex",
                new TokenUsage(25, 5, 10, 1, 300), "code", "file", "src/d.ts",
                null, 3, ReviewModelSource.Explicit),
                TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                var validation = LedgerV3Schema.Value.Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
                Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
            }

            using (var first = JsonDocument.Parse(lines[0]))
            {
                Assert.Equal("policy-default", first.RootElement.GetProperty("modelSource").GetString());
                Assert.Equal("review-sweep-3", first.RootElement.GetProperty("reviewRunId").GetString());
                Assert.Equal(0.5m, first.RootElement.GetProperty("cost").GetProperty("total").GetDecimal());
            }
            using (var second = JsonDocument.Parse(lines[1]))
            {
                Assert.Equal("explicit", second.RootElement.GetProperty("modelSource").GetString());
                Assert.False(second.RootElement.TryGetProperty("reviewRunId", out _));
                Assert.False(second.RootElement.TryGetProperty("cost", out _));
            }

            // Both v3 shapes are queryable next to the older versions, and the report totals the
            // stored cost plus the query-time price of the entry written without one.
            var report = await UsageLedger.QueryAsync(root.FullName, timestamp.AddMinutes(-1), "code",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(2, report.Runs);
            Assert.Contains(report.Recent, entry =>
                entry.RunId == "cli-run-4" && entry.ModelSource == ReviewModelSource.Explicit);
            Assert.NotNull(report.EstimatedCost);
            Assert.True(report.EstimatedCost > 0.5m, "the query-time price of the second entry is added to the stored cost");
            Assert.Equal("USD", report.CostCurrency);
            Assert.Equal(0, report.UnpricedRuns);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task V4EntriesRecordCacheWritesAndPriceEveryInputTariff()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-v4-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
            // 100k fresh + 600k cache reads + 300k cache writes = 1M billed input tokens.
            var tokens = new TokenUsage(1_000_000, 10_000, 600_000, 0, 900, CacheWriteInputTokens: 300_000);
            var cost = UsageLedger.EstimateCost("claude-opus-5", tokens, timestamp);
            // 0.1M * 5 + 0.6M * 0.5 + 0.3M * 6.25 + 0.01M * 25 per million.
            Assert.Equal(2.925m, cost.Total);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "claude-run-1", timestamp, "claude-opus-5", "claude", tokens, "code", "file", "src/a.cs",
                "review-sweep-4", UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit, cost,
                PromptCharacters: 48_000, PriceAccuracy: UsagePriceAccuracy.UnderPriced),
                TestContext.Current.CancellationToken);

            var line = Assert.Single(await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken));
            using (var json = JsonDocument.Parse(line))
            {
                var validation = SchemaCatalogue.Get("usage-ledger.v4.schema.json").Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
                Assert.Equal(4, json.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(300_000, json.RootElement.GetProperty("tokens").GetProperty("cacheWriteInputTokens").GetInt64());
                Assert.Equal(48_000, json.RootElement.GetProperty("promptCharacters").GetInt32());
                // The accuracy flag is derived on read, never persisted.
                Assert.False(json.RootElement.TryGetProperty("priceAccuracy", out _));
            }

            var report = await UsageLedger.QueryAsync(root.FullName, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1_000_000, report.InputTokens);
            Assert.Equal(300_000, report.CacheWriteInputTokens);
            Assert.Equal(300_000, Assert.Single(report.ByModel).CacheWriteInputTokens);
            Assert.Equal(2.925m, report.EstimatedCost);
            Assert.Equal(0, report.UnderPricedRuns);
            Assert.Null(Assert.Single(report.Recent).PriceAccuracy);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task PreV4ClaudeEntriesAreReadWithTheirCacheReadsAsInputAndFlaggedUnderPriced()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-legacy-claude-");
        try
        {
            // The 2026-09-28 evaluation shape: Claude's fresh input_tokens only, cache reads apart,
            // cache writes dropped, and a stored cost that subtracted the reads from the fresh input.
            var timestamp = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "claude-legacy", timestamp, "claude-opus-5", "claude",
                new TokenUsage(2_000, 10_000, 600_000, 0, 900), "code", "file", "src/a.cs",
                "review-sweep-0928", 3, ReviewModelSource.Explicit, new UsageCost(0.251m, "USD", "resolved")),
                TestContext.Current.CancellationToken);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "codex-legacy", timestamp.AddMinutes(1), "gpt-5.6-luna", "codex",
                new TokenUsage(1_000_000, 100_000, 200_000, 0, 900), "code", "file", "src/b.cs",
                "review-sweep-0928", 3, ReviewModelSource.Explicit, new UsageCost(0.284m, "USD", "resolved")),
                TestContext.Current.CancellationToken);

            var report = await UsageLedger.QueryAsync(root.FullName, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, report.UnderPricedRuns);
            var claude = Assert.Single(report.Recent, entry => entry.RunId == "claude-legacy");
            Assert.Equal(UsagePriceAccuracy.UnderPriced, claude.PriceAccuracy);
            Assert.Equal(602_000, claude.Tokens.InputTokens);
            // Re-priced as 2k fresh at 5 + 600k reads at 0.5 + 10k output at 25: still a lower bound.
            Assert.Equal(0.56m, claude.Cost!.Total);
            var codex = Assert.Single(report.Recent, entry => entry.RunId == "codex-legacy");
            Assert.Null(codex.PriceAccuracy);
            Assert.Equal(1_000_000, codex.Tokens.InputTokens);
            Assert.Equal(0.284m, codex.Cost!.Total);
            Assert.Equal(0.844m, report.EstimatedCost);

            // A pre-v4 entry cannot carry the v4 fields.
            await Assert.ThrowsAsync<ArgumentException>(() => UsageLedger.AppendAsync(root.FullName,
                new ReviewUsageEntry("bad", timestamp, "claude-opus-5", "claude",
                    new TokenUsage(1, 1, 0, 0, 1, CacheWriteInputTokens: 1), "code", "file", "src/a.cs",
                    null, 3, ReviewModelSource.Explicit), TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void EstimateCostPricesACataloguedModelInsideItsValidityAndNamesTheReasonOtherwise()
    {
        var tokens = new TokenUsage(1_000_000, 100_000, 200_000, 0, 1000);
        var insideValidity = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        var priced = UsageLedger.EstimateCost("gpt-5.6-luna", tokens, insideValidity);
        Assert.Equal("resolved", priced.Status);
        Assert.Equal("USD", priced.Currency);
        // 800k uncached input at 1.00, 200k cached input at 0.10, 100k output at 6.00 per million.
        Assert.Equal(1.42m, priced.Total);

        var unknown = UsageLedger.EstimateCost(ReviewModelSource.RunnerDefault, tokens, insideValidity);
        Assert.Null(unknown.Total);
        Assert.Equal("unknownModel", unknown.Status);

        // The snapshot's later entry (the 2026-07-30 price cut) supersedes the launch price.
        var afterPriceCut = UsageLedger.EstimateCost("gpt-5.6-luna", tokens, new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("resolved", afterPriceCut.Status);
        Assert.Equal(0.284m, afterPriceCut.Total);

        // GPT-5 has a confirmed historical tariff: 0.8*1.25 + 0.2*0.125 + 0.1*10.
        var historical = UsageLedger.EstimateCost("gpt-5", tokens, insideValidity);
        Assert.Equal("resolved", historical.Status);
        Assert.Equal("USD", historical.Currency);
        Assert.Equal(2.025m, historical.Total);

        // Before the first dated price the model is known, but its cost remains unknown, never zero.
        var launch = new DateTimeOffset(2025, 8, 7, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(2.025m, UsageLedger.EstimateCost("gpt-5", tokens, launch).Total);
        var unpriced = UsageLedger.EstimateCost("gpt-5", tokens, launch.AddTicks(-1));
        Assert.Null(unpriced.Total);
        Assert.Equal("noPriceForDate", unpriced.Status);
    }

    [Theory]
    [InlineData("gpt-6-astra", 3, "13.2")]
    [InlineData("claude-fable-5-1", 1, "13.05")]
    public void Newly_supported_models_use_their_dated_input_cache_and_output_tariffs(
        string modelId, int septemberLaunchDay, string expectedUsd)
    {
        var tokens = new TokenUsage(1_000_000, 100_000, 200_000, 0, 1000);
        var launch = new DateTimeOffset(2026, 9, septemberLaunchDay, 0, 0, 0, TimeSpan.Zero);
        var price = UsageLedger.EstimateCost(modelId, tokens, launch);

        Assert.Equal("resolved", price.Status);
        Assert.Equal("USD", price.Currency);
        Assert.Equal(decimal.Parse(expectedUsd, System.Globalization.CultureInfo.InvariantCulture), price.Total);

        var beforeLaunch = UsageLedger.EstimateCost(modelId, tokens, launch.AddTicks(-1));
        Assert.Null(beforeLaunch.Total);
        Assert.Equal("noPriceForDate", beforeLaunch.Status);
    }
}
