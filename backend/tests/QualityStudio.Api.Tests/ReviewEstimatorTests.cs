using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewEstimatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithoutMatchingHistoryTheEstimateUsesPromptSizeAndNeverBorrowsAnotherRoute()
    {
        // Codex history and Claude history of another model are not evidence for claude/claude-opus-5.
        ReviewUsageEntry[] history =
        [
            Entry("codex", "gpt-5.6-luna", new TokenUsage(10_000, 40_000, 0, 0, 1), 4_000),
            Entry("claude", "claude-sonnet-5", new TokenUsage(10_000, 40_000, 0, 0, 1), 4_000),
        ];

        var estimate = ReviewEstimator.Estimate([40_000, 8_000], history, "claude", "claude-opus-5",
            ReviewPriceCatalog.Default, Now, files: 1, expectedFreshSkips: 0);

        Assert.Equal(ReviewEstimateBasis.PromptSize, estimate.Basis);
        Assert.Equal(0, estimate.HistorySamples);
        Assert.Equal(12_000, estimate.InputTokens);
        Assert.Equal(2_400, estimate.OutputTokens);
        Assert.Equal(0, estimate.CachedInputTokens);
        Assert.Contains("No recorded claude/claude-opus-5 operations", estimate.Method);
        // 12k input at 5 + 2.4k output at 25 per million.
        Assert.Equal(0.12m, estimate.Cost);
    }

    [Fact]
    public void MatchingHistoryWithPromptSizesScalesEveryTokenCategoryPerPromptCharacter()
    {
        ReviewUsageEntry[] history =
        [
            // Per 10k prompt characters: 1k fresh, 60k cache reads, 30k cache writes, 1k output.
            Entry("claude", "claude-opus-5", new TokenUsage(91_000, 1_000, 60_000, 0, 1, 30_000), 10_000),
            Entry("claude", "claude-opus-5", new TokenUsage(182_000, 2_000, 120_000, 0, 1, 60_000), 20_000),
            Entry("codex", "claude-opus-5", new TokenUsage(1, 1_000_000, 0, 0, 1), 1),
        ];

        var estimate = ReviewEstimator.Estimate([10_000, 30_000], history, "claude", "claude-opus-5",
            ReviewPriceCatalog.Default, Now, files: 2, expectedFreshSkips: 0);

        Assert.Equal(ReviewEstimateBasis.HistoryPromptRatio, estimate.Basis);
        Assert.Equal(2, estimate.HistorySamples);
        Assert.Equal(364_000, estimate.InputTokens);
        Assert.Equal(240_000, estimate.CachedInputTokens);
        Assert.Equal(120_000, estimate.CacheWriteInputTokens);
        Assert.Equal(4_000, estimate.OutputTokens);
        // 4k fresh at 5 + 240k reads at 0.5 + 120k writes at 6.25 + 4k output at 25 per million.
        Assert.Equal(0.99m, estimate.Cost);
        Assert.Contains("2 recorded claude/claude-opus-5 operation(s)", estimate.Method);
    }

    [Fact]
    public void MatchingHistoryWithoutPromptSizesUsesTheMeanOperation()
    {
        ReviewUsageEntry[] history =
        [
            Entry("claude", "claude-opus-5", new TokenUsage(100_000, 2_000, 50_000, 0, 1, 40_000), null),
            Entry("claude", "claude-opus-5", new TokenUsage(300_000, 4_000, 150_000, 0, 1, 120_000), null),
        ];

        var estimate = ReviewEstimator.Estimate([1, 2, 3], history, "claude", "claude-opus-5",
            ReviewPriceCatalog.Default, Now, files: 3, expectedFreshSkips: 1);

        Assert.Equal(ReviewEstimateBasis.HistoryPerOperation, estimate.Basis);
        Assert.Equal(600_000, estimate.InputTokens);
        Assert.Equal(300_000, estimate.CachedInputTokens);
        Assert.Equal(240_000, estimate.CacheWriteInputTokens);
        Assert.Equal(9_000, estimate.OutputTokens);
        Assert.Equal(1, estimate.ExpectedFreshSkips);
    }

    [Fact]
    public void UnderPricedHistoryIsNotEvidence()
    {
        ReviewUsageEntry[] history =
        [
            Entry("claude", "claude-opus-5", new TokenUsage(602_000, 10_000, 600_000, 0, 1), 10_000)
                with { PriceAccuracy = UsagePriceAccuracy.UnderPriced },
        ];

        var estimate = ReviewEstimator.Estimate([4_000], history, "claude", "claude-opus-5",
            ReviewPriceCatalog.Default, Now, files: 1, expectedFreshSkips: 0);

        Assert.Equal(ReviewEstimateBasis.PromptSize, estimate.Basis);
        Assert.Equal(0, estimate.HistorySamples);
    }

    [Fact]
    public async Task LedgerHistoryOfTheEvaluationDayDoesNotFeedTheEstimate()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-estimate-");
        try
        {
            await UsageLedger.AppendAsync(root.FullName, new ReviewUsageEntry(
                "claude-legacy", new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero), "claude-opus-5", "claude",
                new TokenUsage(2_000, 10_000, 600_000, 0, 900), "code", "file", "src/a.cs",
                "review-sweep-0928", 3, ReviewModelSource.Explicit), TestContext.Current.CancellationToken);
            await UsageLedger.AppendAsync(root.FullName, Entry("claude", "claude-opus-5",
                new TokenUsage(91_000, 1_000, 60_000, 0, 1, 30_000), 10_000), TestContext.Current.CancellationToken);

            var history = await UsageLedger.ReadAsync(root.FullName, kind: "code",
                cancellationToken: TestContext.Current.CancellationToken);
            var estimate = ReviewEstimator.Estimate([10_000], history, "claude", "claude-opus-5",
                ReviewPriceCatalog.Default, Now, files: 1, expectedFreshSkips: 0);

            Assert.Equal(ReviewEstimateBasis.HistoryPromptRatio, estimate.Basis);
            Assert.Equal(1, estimate.HistorySamples);
            Assert.Equal(91_000, estimate.InputTokens);
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static ReviewUsageEntry Entry(string cliType, string model, TokenUsage tokens, int? promptCharacters) =>
        new(Guid.NewGuid().ToString("N"), Now.AddDays(-1), model, cliType, tokens, "code", "file", "src/a.cs",
            "review-sweep", UsageLedger.CurrentSchemaVersion, ReviewModelSource.Explicit,
            PromptCharacters: promptCharacters);
}
