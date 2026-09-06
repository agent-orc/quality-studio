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
    public void EstimateCostPricesACataloguedModelInsideItsValidityAndNamesTheReasonOtherwise()
    {
        var tokens = new TokenUsage(1_000_000, 100_000, 200_000, 0, 1000);
        var insideValidity = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        var priced = UsageLedger.EstimateCost("gpt-5.6-luna", tokens, insideValidity);
        Assert.Equal("resolved", priced.Status);
        Assert.Equal("USD", priced.Currency);
        // 800k uncached input at 1.00, 200k cached input at 0.10, 100k output at 6.00 per million.
        Assert.NotNull(priced.Total);
        Assert.InRange(priced.Total.Value, 1.40m, 1.45m);

        var unknown = UsageLedger.EstimateCost(ReviewModelSource.RunnerDefault, tokens, insideValidity);
        Assert.Null(unknown.Total);
        Assert.Equal("unknownModel", unknown.Status);

        // The snapshot's later entry (the 2026-07-30 price cut) supersedes the launch price.
        var afterPriceCut = UsageLedger.EstimateCost("gpt-5.6-luna", tokens, new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("resolved", afterPriceCut.Status);
        Assert.NotNull(afterPriceCut.Total);
        Assert.InRange(afterPriceCut.Total.Value, 0.28m, 0.29m);

        // A model the snapshot lists without a price history is known but unpriced, never zero.
        var unpriced = UsageLedger.EstimateCost("gpt-5", tokens, insideValidity);
        Assert.Null(unpriced.Total);
        Assert.Equal("noPriceForDate", unpriced.Status);
    }
}
