using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class UsageLedgerTests
{
    // JsonSchema.Net registers each schema's $id globally and refuses a second registration, so
    // the current schema is parsed once for every validation in this class.
    private static readonly Lazy<JsonSchema> LedgerSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "usage-ledger.v4.schema.json"))));

    [Fact]
    public async Task CurrentEntriesAttributeEveryOperationToAModelSourceAndCarryTheirCost()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-v4-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-3", timestamp, "gpt-5.6-luna", "codex",
                new TokenUsage(50, 10, 20, 2, 600), "code", "file", "src/c.ts",
                "review-sweep-3", UsageLedger.CurrentSchemaVersion, ReviewModelSource.PolicyDefault,
                new UsageCost(0.5m, "USD", "resolved")),
                TestContext.Current.CancellationToken);
            // A standalone CLI review has no sweep id but still names its model source; an entry
            // without a stored cost is priced at query time.
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "cli-run-4", timestamp.AddMinutes(1), "gpt-5.6-sol", "codex",
                new TokenUsage(25, 5, 10, 1, 300), "code", "file", "src/d.ts",
                null, UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit),
                TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                using var json = JsonDocument.Parse(line);
                var validation = LedgerSchema.Value.Evaluate(json.RootElement,
                    new EvaluationOptions { OutputFormat = OutputFormat.List });
                Assert.True(validation.IsValid, validation.ToString());
                Assert.Equal(4, json.RootElement.GetProperty("schemaVersion").GetInt32());
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

            // Both shapes are queryable next to the older versions, and the report totals the
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

    [Theory]
    [InlineData("claude-opus-5-5", 800_000, "5.24")]
    [InlineData("gpt-6-sol", 1_000_000, "2.64")]
    [InlineData("gpt-6-luna", 1_000_000, "0.132")]
    public async Task Query_reprices_v3_unknown_model_entries_on_read(
        string modelId, long inputTokens, string expectedUsd)
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-reprice-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "explicit-run", timestamp, modelId, modelId.StartsWith("claude", StringComparison.Ordinal) ? "claude" : "codex",
                new TokenUsage(inputTokens, 100_000, 200_000, 0, 1000), "code", "file", "src/a.cs",
                null, 3, ReviewModelSource.Explicit,
                new UsageCost(null, null, "unknownModel")), TestContext.Current.CancellationToken);

            var report = await UsageLedger.QueryAsync(root.FullName, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, report.UnpricedRuns);
            Assert.Equal(decimal.Parse(expectedUsd, System.Globalization.CultureInfo.InvariantCulture), report.EstimatedCost);
            var entry = Assert.Single(report.Recent);
            Assert.Equal(ReviewModelSource.Explicit, entry.ModelSource);
            Assert.Equal("resolved", entry.Cost!.Status);
            Assert.Equal(report.EstimatedCost, entry.Cost.Total);
            var line = Assert.Single(await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken));
            Assert.Contains("\"status\":\"unknownModel\"", line);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ClaudeCacheWritesAreRecordedValidatedAndPricedAtTheCacheWriteRate()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-cache-write-");
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
            // 1M input = 100k fresh + 700k cache read + 200k cache write; 50k output.
            var tokens = new TokenUsage(1_000_000, 50_000, 700_000, 0, 900, 200_000);
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "claude-run-1", timestamp, "claude-opus-5", "claude", tokens, "code", "file", "src/a.cs",
                "review-sweep-9", UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit,
                UsageLedger.EstimateCost("claude-opus-5", tokens, timestamp)),
                TestContext.Current.CancellationToken);

            var line = Assert.Single(await File.ReadAllLinesAsync(
                UsageLedger.GetLedgerPath(root.FullName, timestamp), TestContext.Current.CancellationToken));
            using var json = JsonDocument.Parse(line);
            var validation = LedgerSchema.Value.Evaluate(json.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(validation.IsValid, validation.ToString());
            Assert.Equal(200_000, json.RootElement.GetProperty("tokens").GetProperty("cacheWriteInputTokens").GetInt64());

            // 0.1M fresh at 5.00 + 0.7M read at 0.50 + 0.2M write at 6.25 + 0.05M output at 25.00.
            Assert.Equal(3.35m, json.RootElement.GetProperty("cost").GetProperty("total").GetDecimal());

            var report = await UsageLedger.QueryAsync(root.FullName,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1_000_000, report.InputTokens);
            Assert.Equal(200_000, report.CacheWriteInputTokens);
            Assert.Equal(200_000, Assert.Single(report.ByModel).CacheWriteInputTokens);
            Assert.Equal(3.35m, report.EstimatedCost);
            Assert.Equal(0, report.UnderPricedRuns);
            Assert.Null(Assert.Single(report.Recent).AccountingNote);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task PreSchema4ClaudeEntriesAreNormalizedRepricedAndFlaggedAsUnderPriced()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-usage-legacy-");
        try
        {
            // The shape the 2026-09-28 sweep wrote: v3, Claude fresh input only, cache reads beside
            // it, no cache writes, and a stored cost that clamped the cache reads to the fresh input.
            var legacyLine = """
                {"runId":"quality-legacy","timestamp":"2026-09-28T14:02:00+00:00","model":"claude-opus-5","cliType":"claude","tokens":{"inputTokens":1000,"outputTokens":20000,"cachedInputTokens":400000,"reasoningOutputTokens":0,"durationMs":60000},"kind":"code","level":"file","path":"src/a.cs","reviewRunId":"review-0928","schemaVersion":3,"modelSource":"explicit","cost":{"total":0.5005,"currency":"USD","status":"resolved"}}
                """;
            var codexLine = """
                {"runId":"codex-legacy","timestamp":"2026-09-28T14:05:00+00:00","model":"gpt-5.6-luna","cliType":"codex","tokens":{"inputTokens":100000,"outputTokens":1000,"cachedInputTokens":50000,"reasoningOutputTokens":0,"durationMs":6000},"kind":"code","level":"file","path":"src/b.cs","reviewRunId":"review-0928","schemaVersion":3,"modelSource":"explicit"}
                """;
            var timestamp = new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);
            var path = UsageLedger.GetLedgerPath(root.FullName, timestamp);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, legacyLine.Trim() + "\n" + codexLine.Trim() + "\n",
                TestContext.Current.CancellationToken);

            var report = await UsageLedger.QueryAsync(root.FullName,
                cancellationToken: TestContext.Current.CancellationToken);

            var claude = Assert.Single(report.Recent, entry => entry.CliType == "claude");
            // Cache reads join the input so the history counts all input the model processed.
            Assert.Equal(401_000, claude.Tokens.InputTokens);
            Assert.Null(claude.Tokens.CacheWriteInputTokens);
            Assert.Equal(UsageLedger.UnderPricedNote, claude.AccountingNote);
            Assert.True(UsageLedger.IsUnderPriced(claude));
            // Repriced as 1k fresh at 5.00 + 400k read at 0.50 + 20k output at 25.00; still a lower
            // bound because the cache writes were never captured.
            Assert.Equal(0.705m, claude.Cost!.Total);

            var codex = Assert.Single(report.Recent, entry => entry.CliType == "codex");
            Assert.Equal(100_000, codex.Tokens.InputTokens);
            Assert.Null(codex.AccountingNote);
            Assert.False(UsageLedger.IsUnderPriced(codex));

            Assert.Equal(1, report.UnderPricedRuns);
            Assert.Equal(501_000, report.InputTokens);
            // The file itself is append-only history and stays untouched.
            Assert.Contains("\"inputTokens\":1000,", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void PricingUsageSplitsAllInputIntoFreshCacheReadAndCacheWrite()
    {
        var split = UsageLedger.ToPricingUsage(new TokenUsage(1_000, 50, 600, 0, 0, 300));
        Assert.Equal(100, split.Input);
        Assert.Equal(600, split.CacheRead);
        Assert.Equal(300, split.CacheWrite);
        Assert.Equal(50, split.Output);

        // Inconsistent counts never go negative or price more input than was recorded.
        var clamped = UsageLedger.ToPricingUsage(new TokenUsage(500, null, 400, null, 0, 400));
        Assert.Equal(0, clamped.Input);
        Assert.Equal(400, clamped.CacheRead);
        Assert.Equal(100, clamped.CacheWrite);
        Assert.Equal(0, clamped.Output);
    }
}
