namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewUsageForecasterTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly PlannedReviewOperation[] TwoFilesAndAggregate =
    [
        new(ReviewLevel.File, 40_000),
        new(ReviewLevel.File, 40_000),
        new(ReviewLevel.Module, 8_000),
    ];

    [Fact]
    public void WithoutMatchingHistoryTheForecastUsesPromptSizeAndIgnoresOtherRoutes()
    {
        // Plenty of history, but none for this CLI and model: it must not leak into the estimate.
        var history = new[]
        {
            Entry("codex", "gpt-5.6-sol", "file", new TokenUsage(900_000, 400_000, 0, 0, 0)),
            Entry("claude", "claude-sonnet-5", "file", new TokenUsage(900_000, 400_000, 0, 0, 0, 0)),
            Entry("claude", "claude-opus-5", "file", new TokenUsage(900_000, 400_000, 0, 0, 0, 0), kind: "security"),
        };

        var forecast = ReviewUsageForecaster.Forecast(history, "claude", "claude-opus-5", "code",
            TwoFilesAndAggregate, At);

        Assert.Equal(ReviewUsageForecaster.PromptSizeBasis, forecast.Basis);
        Assert.Equal(0, forecast.HistorySamples);
        Assert.Equal(22_000, forecast.InputTokens);
        Assert.Equal(4_400, forecast.OutputTokens);
        Assert.Equal(0, forecast.CachedInputTokens);
        Assert.Equal(0, forecast.CacheWriteInputTokens);
        // 22k fresh input at 5.00 + 4.4k output at 25.00 per million.
        Assert.Equal(0.22m, forecast.Cost.Total);
        Assert.StartsWith("Prompt size:", forecast.Method, StringComparison.Ordinal);
        Assert.Contains("claude/claude-opus-5", forecast.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingHistoryPredictsPerOperationMeansByLevelWithCacheClassesPriced()
    {
        var history = new[]
        {
            Entry("claude", "claude-opus-5", "file", new TokenUsage(100_000, 4_000, 60_000, 0, 0, 30_000)),
            Entry("claude", "claude-opus-5", "file", new TokenUsage(300_000, 6_000, 220_000, 0, 0, 50_000)),
            Entry("claude", "claude-opus-5", "module", new TokenUsage(50_000, 2_000, 20_000, 0, 0, 20_000)),
            // Other routes and an under-priced legacy Claude entry are not evidence for this route.
            Entry("codex", "gpt-5.6-sol", "file", new TokenUsage(5_000_000, 900_000, 0, 0, 0)),
            Entry("claude", "claude-opus-5", "file", new TokenUsage(9_000, 9_000_000, 0, 0, 0), schemaVersion: 3),
        };

        var forecast = ReviewUsageForecaster.Forecast(history, "CLAUDE", "claude-opus-5", "code",
            TwoFilesAndAggregate, At);

        Assert.Equal(ReviewUsageForecaster.HistoryBasis, forecast.Basis);
        Assert.Equal(3, forecast.HistorySamples);
        // Two file operations at the file mean (200k in, 140k read, 40k write, 5k out) plus one
        // aggregate at the aggregate sample (50k in, 20k read, 20k write, 2k out).
        Assert.Equal(450_000, forecast.InputTokens);
        Assert.Equal(300_000, forecast.CachedInputTokens);
        Assert.Equal(100_000, forecast.CacheWriteInputTokens);
        Assert.Equal(12_000, forecast.OutputTokens);
        // 50k fresh at 5.00 + 300k read at 0.50 + 100k write at 6.25 + 12k output at 25.00.
        Assert.Equal(1.325m, forecast.Cost.Total);
        Assert.Equal("resolved", forecast.Cost.Status);
        Assert.StartsWith("History:", forecast.Method, StringComparison.Ordinal);
        Assert.Contains("3 recorded claude/claude-opus-5 code operation(s) (2 file, 1 aggregate)", forecast.Method,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAggregateWithoutAggregateHistoryUsesTheRouteMean()
    {
        var history = new[]
        {
            Entry("codex", "gpt-5.6-luna", "file", new TokenUsage(10_000, 1_000, 4_000, 0, 0)),
            Entry("codex", "luna", "file", new TokenUsage(30_000, 3_000, 8_000, 0, 0)),
        };

        var forecast = ReviewUsageForecaster.Forecast(history, "codex", "gpt-5.6-luna", "code",
            [new PlannedReviewOperation(ReviewLevel.Module, 1_000)], At);

        // The alias "luna" names the same catalogue model, so both entries match.
        Assert.Equal(2, forecast.HistorySamples);
        Assert.Equal(20_000, forecast.InputTokens);
        Assert.Equal(6_000, forecast.CachedInputTokens);
        Assert.Equal(0, forecast.CacheWriteInputTokens);
        Assert.Equal(2_000, forecast.OutputTokens);
    }

    [Fact]
    public void ARunnerDefaultRouteLearnsOnlyFromRunnerDefaultOperationsOfItsCli()
    {
        var history = new[]
        {
            Entry("gemini", "gemini-3-pro", "file", new TokenUsage(10_000, 1_000, 0, 0, 0),
                modelSource: ReviewModelSource.RunnerDefault),
            Entry("gemini", "gemini-3-pro", "file", new TokenUsage(90_000, 9_000, 0, 0, 0),
                modelSource: ReviewModelSource.Explicit),
        };

        var forecast = ReviewUsageForecaster.Forecast(history, "gemini", null, "code",
            [new PlannedReviewOperation(ReviewLevel.File, 1_000)], At);

        Assert.Equal(ReviewUsageForecaster.HistoryBasis, forecast.Basis);
        Assert.Equal(1, forecast.HistorySamples);
        Assert.Equal(10_000, forecast.InputTokens);
        Assert.Null(forecast.Cost.Total);
        Assert.Equal("unknownModel", forecast.Cost.Status);
    }

    private static ReviewUsageEntry Entry(string cli, string model, string level, TokenUsage tokens,
        string kind = "code", int schemaVersion = UsageLedger.CurrentSchemaVersion,
        string modelSource = ReviewModelSource.Explicit) =>
        new($"run-{Guid.NewGuid():N}", At.AddDays(-1), model, cli, tokens, kind, level, "src/x.cs",
            "review-1", schemaVersion, modelSource);
}
