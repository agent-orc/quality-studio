using System.Globalization;
using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Pins the Token Economy 0.3.8 price sync: every newly priced model is repriced at query time from
/// its dated tariff, and prompt-size tiers never let summed operation usage claim the cheap tier.
/// </summary>
public sealed class PromptSizeTierPricingTests
{
    // 80k input = 60k fresh + 20k cache read; 10k output. Below every prompt-size tier.
    private static readonly TokenUsage SmallOperation = new(80_000, 10_000, 20_000, 0, 1000);

    [Theory]
    [InlineData("claude-haiku-5-5", "claude", "2026-10-07", "2026-10-07", "0.0112")]
    [InlineData("gemini-4-argon", "antigravity", "2026-10-13", "2026-10-13", "0.222")]
    [InlineData("gemini-4-argon", "antigravity", "2026-10-13", "2026-12-01", "0.444")]
    [InlineData("gemini-3.8-flash", "antigravity", "2026-09-02", "2026-09-02", "0.084")]
    [InlineData("gemini-3.8-flash", "antigravity", "2026-09-02", "2027-01-01", "0.168")]
    [InlineData("gpt-6.1-sol", "codex", "2026-09-29", "2026-09-29", "0.222")]
    [InlineData("claude-sonnet-5-5", "claude", "2026-09-28", "2026-09-28", "0.224")]
    [InlineData("claude-opus-5-5", "claude", "2026-09-22", "2026-09-22", "0.444")]
    [InlineData("gpt-6-sol", "codex", "2026-09-22", "2026-09-22", "0.224")]
    [InlineData("gpt-6-luna", "codex", "2026-09-22", "2026-09-22", "0.0112")]
    public async Task Entries_without_a_stored_cost_are_repriced_at_query_time_from_the_dated_tariff(
        string modelId, string cliType, string launchDay, string operationDay, string expectedUsd)
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-te038-");
        try
        {
            var timestamp = Day(operationDay).AddHours(10);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                    $"{modelId}-run", timestamp, modelId, cliType, SmallOperation, "code", "file", "src/a.cs",
                    "review-sweep-te038", UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit),
                TestContext.Current.CancellationToken);

            var report = await UsageLedger.QueryAsync(root.FullName,
                cancellationToken: TestContext.Current.CancellationToken);

            var expected = decimal.Parse(expectedUsd, CultureInfo.InvariantCulture);
            var cost = Assert.Single(report.Recent).Cost!;
            Assert.Equal("resolved", cost.Status);
            Assert.Equal(expected, cost.Total);
            Assert.Equal("USD", cost.Currency);
            Assert.Equal(expected, report.EstimatedCost);
            Assert.Equal(0, report.UnpricedRuns);
            Assert.Equal("noPriceForDate",
                UsageLedger.EstimateCost(modelId, SmallOperation, Day(launchDay).AddTicks(-1)).Status);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void A_summed_prompt_below_the_first_tier_proves_the_base_tier_for_every_request()
    {
        // 100,000 is the last base-tier prompt: Haiku 5.5's higher tariff starts at 100,001.
        var atThreshold = UsageLedger.EstimateCost("claude-haiku-5-5",
            new TokenUsage(100_000, 10_000, 0, 0, 1000), Day("2026-10-08"));

        Assert.Equal("resolved", atThreshold.Status);
        Assert.Equal(0.015m, atThreshold.Total);
        Assert.Null(atThreshold.LowerBound);
        Assert.Null(atThreshold.UpperBound);
    }

    [Fact]
    public void A_summed_prompt_above_the_tier_is_ambiguous_with_both_tier_totals_as_bounds()
    {
        // 300k input = 200k fresh + 60k cache read + 40k cache write; 10k output. The sum could be
        // one request billed wholly at the higher rate or several small requests at the base rate.
        var tokens = new TokenUsage(300_000, 10_000, 60_000, 0, 1000, 40_000);

        var cost = UsageLedger.EstimateCost("claude-haiku-5-5", tokens, Day("2026-10-08"));

        Assert.Equal(ReviewPriceCatalog.AmbiguousPromptSize, cost.Status);
        Assert.Null(cost.Total);
        Assert.Equal("USD", cost.Currency);
        // Base: 0.2M at 0.10 + 0.06M at 0.01 + 0.04M at 0.125 + 0.01M at 0.50.
        Assert.Equal(0.0306m, cost.LowerBound);
        // Higher tier for the whole request: 0.2M at 0.50 + 0.06M at 0.05 + 0.04M at 0.625 + 0.01M at 2.50.
        Assert.Equal(0.153m, cost.UpperBound);

        // GPT-6.1 Sol's tier starts above 272K; a model without tiers stays resolved at any size.
        Assert.Equal("resolved", UsageLedger.EstimateCost("gpt-6.1-sol",
            new TokenUsage(272_000, 10_000, 0, 0, 1000), Day("2026-10-08")).Status);
        var sol = UsageLedger.EstimateCost("gpt-6.1-sol", new TokenUsage(272_001, 10_000, 0, 0, 1000), Day("2026-10-08"));
        Assert.Equal(ReviewPriceCatalog.AmbiguousPromptSize, sol.Status);
        Assert.True(sol.UpperBound > sol.LowerBound);
        Assert.Equal("resolved", UsageLedger.EstimateCost("gemini-4-argon", tokens, Day("2026-10-14")).Status);
    }

    [Fact]
    public void An_unknown_input_count_leaves_a_tiered_price_ambiguous_without_bounds()
    {
        var cost = UsageLedger.EstimateCost("claude-haiku-5-5",
            new TokenUsage(null, 10_000, null, null, 1000), Day("2026-10-08"));

        Assert.Equal(ReviewPriceCatalog.AmbiguousPromptSize, cost.Status);
        Assert.Null(cost.Total);
        Assert.Null(cost.LowerBound);
        Assert.Null(cost.UpperBound);
    }

    [Fact]
    public async Task An_ambiguous_entry_is_reported_unpriced_and_its_bounds_validate_against_the_ledger_schema()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-tier-");
        try
        {
            var timestamp = Day("2026-10-08").AddHours(9);
            var tokens = new TokenUsage(300_000, 10_000, 60_000, 0, 1000, 40_000);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                    "haiku-large", timestamp, "claude-haiku-5-5", "claude", tokens, "code", "file", "src/a.cs",
                    "review-sweep-tier", UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit,
                    UsageLedger.EstimateCost("claude-haiku-5-5", tokens, timestamp)),
                TestContext.Current.CancellationToken);

            var line = Assert.Single(await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(line);
            var validation = SchemaCatalogue.Get("usage-ledger.v4.schema.json").Evaluate(json.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
            var stored = json.RootElement.GetProperty("cost");
            Assert.Equal(JsonValueKind.Null, stored.GetProperty("total").ValueKind);
            Assert.Equal(0.153m, stored.GetProperty("upperBound").GetDecimal());

            var report = await UsageLedger.QueryAsync(root.FullName,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Null(report.EstimatedCost);
            Assert.Equal(1, report.UnpricedRuns);
            Assert.Equal(ReviewPriceCatalog.AmbiguousPromptSize, Assert.Single(report.Recent).Cost!.Status);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static DateTimeOffset Day(string isoDate) =>
        DateTimeOffset.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
