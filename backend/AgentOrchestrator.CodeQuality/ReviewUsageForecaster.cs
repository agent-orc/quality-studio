using ModelPriceCatalog = CodingAgentRunner.Pricing.ModelPriceCatalog;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One operation a planned review will run: its level and rendered prompt size.</summary>
public sealed record PlannedReviewOperation(ReviewLevel Level, int PromptCharacters);

/// <summary>
/// Predicted usage of a planned review. <see cref="Basis"/> names where the figures come from
/// (<see cref="ReviewUsageForecaster.HistoryBasis"/> or <see cref="ReviewUsageForecaster.PromptSizeBasis"/>)
/// and <see cref="Method"/> explains it in words for the preflight sheet.
/// </summary>
public sealed record ReviewUsageForecast(
    string Basis,
    int HistorySamples,
    long InputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    long OutputTokens,
    UsageCost Cost,
    string Method);

/// <summary>
/// Predicts a review's tokens and cost from recorded operations of the same CLI and model only.
/// Other CLIs and models cache, read files and answer differently, so mixing them in (as the
/// 2026-09-28 preflight did, predicting USD 56.59 for a USD 10.61 run) is worse than no history;
/// without matching history the forecast falls back to the rendered prompt size.
/// </summary>
public static class ReviewUsageForecaster
{
    public const string HistoryBasis = "history";
    public const string PromptSizeBasis = "prompt-size";
    public const decimal FallbackOutputRatio = 0.20m;

    public static ReviewUsageForecast Forecast(
        IEnumerable<ReviewUsageEntry> history,
        string cliType,
        string? model,
        string kind,
        IReadOnlyList<PlannedReviewOperation> operations,
        DateTimeOffset at,
        ModelPriceCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(operations);
        var prices = catalog ?? ReviewPriceCatalog.Default;
        var samples = history.Where(entry => Matches(entry, cliType, model, kind, prices)).ToArray();
        var route = $"{cliType}/{model ?? ReviewModelSource.RunnerDefault}";
        var priceModel = model ?? ReviewModelSource.RunnerDefault;

        if (samples.Length == 0)
        {
            var input = operations.Sum(operation => PromptTokens(operation.PromptCharacters));
            var output = operations.Sum(operation =>
                Math.Max(1L, (long)Math.Ceiling(PromptTokens(operation.PromptCharacters) * FallbackOutputRatio)));
            var tokens = new TokenUsage(input, output, 0, null, 0, 0);
            return new ReviewUsageForecast(PromptSizeBasis, 0, input, 0, 0, output,
                UsageLedger.EstimateCost(priceModel, tokens, at, prices),
                $"Prompt size: no recorded {route} {kind} operations yet. Input is rendered prompt characters / 4 " +
                $"priced as fresh input; output assumes {FallbackOutputRatio:P0} of input. Agent file reads and " +
                "prompt caching are not predicted, so expect deviation.");
        }

        var fileSamples = samples.Where(IsFileLevel).ToArray();
        var aggregateSamples = samples.Where(sample => !IsFileLevel(sample)).ToArray();
        long totalInput = 0, totalCacheRead = 0, totalCacheWrite = 0, totalOutput = 0;
        foreach (var operation in operations)
        {
            var levelSamples = operation.Level == ReviewLevel.File ? fileSamples : aggregateSamples;
            var basis = levelSamples.Length > 0 ? levelSamples : samples;
            totalInput += Mean(basis, sample => sample.Tokens.InputTokens);
            totalCacheRead += Mean(basis, sample => sample.Tokens.CachedInputTokens);
            totalCacheWrite += Mean(basis, sample => sample.Tokens.CacheWriteInputTokens);
            totalOutput += Mean(basis, sample => sample.Tokens.OutputTokens);
        }
        totalCacheRead = Math.Min(totalCacheRead, totalInput);
        totalCacheWrite = Math.Min(totalCacheWrite, totalInput - totalCacheRead);
        var forecast = new TokenUsage(totalInput, totalOutput, totalCacheRead, null, 0, totalCacheWrite);
        return new ReviewUsageForecast(HistoryBasis, samples.Length, totalInput, totalCacheRead, totalCacheWrite,
            totalOutput, UsageLedger.EstimateCost(priceModel, forecast, at, prices),
            $"History: mean tokens of {samples.Length} recorded {route} {kind} operation(s) " +
            $"({fileSamples.Length} file, {aggregateSamples.Length} aggregate) per planned operation; " +
            "input includes cache reads and writes, each priced at its catalogue rate.");
    }

    /// <summary>
    /// A sample teaches the forecast only when it ran on the same CLI and model, reported usage,
    /// and recorded all billed input (pre-schema-4 Claude entries missed cache writes).
    /// </summary>
    internal static bool Matches(ReviewUsageEntry entry, string cliType, string? model, string kind,
        ModelPriceCatalog prices) =>
        string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Kind, kind, StringComparison.Ordinal) &&
        (model is null
            ? string.Equals(entry.ModelSource, ReviewModelSource.RunnerDefault, StringComparison.Ordinal)
            : SameModel(entry.Model, model, prices)) &&
        !UsageLedger.IsUnderPriced(entry) &&
        entry.Tokens.InputTokens is > 0 && entry.Tokens.OutputTokens is >= 0;

    private static bool SameModel(string recorded, string requested, ModelPriceCatalog prices)
    {
        if (string.Equals(recorded, requested, StringComparison.OrdinalIgnoreCase)) return true;
        var recordedListing = prices.Find(recorded);
        return recordedListing is not null &&
               string.Equals(recordedListing.ModelId, prices.Find(requested)?.ModelId, StringComparison.Ordinal);
    }

    private static bool IsFileLevel(ReviewUsageEntry entry) =>
        string.Equals(entry.Level, "file", StringComparison.OrdinalIgnoreCase);

    private static long PromptTokens(int characters) => (long)Math.Ceiling(Math.Max(0, characters) / 4m);

    private static long Mean(IReadOnlyCollection<ReviewUsageEntry> samples, Func<ReviewUsageEntry, long?> selector) =>
        (long)Math.Ceiling(samples.Average(sample => (decimal)Math.Max(0, selector(sample) ?? 0)));
}
