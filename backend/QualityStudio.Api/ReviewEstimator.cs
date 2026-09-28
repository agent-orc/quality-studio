using AgentOrchestrator.CodeQuality;
using ModelPriceCatalog = CodingAgentRunner.Pricing.ModelPriceCatalog;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;

namespace QualityStudio.Api;

/// <summary>Where a review estimate's token figures come from.</summary>
public static class ReviewEstimateBasis
{
    /// <summary>
    /// Recorded operations of the same CLI and model that know their prompt size: each token
    /// category (fresh input, cache reads, cache writes, output) scales with the prompt characters.
    /// </summary>
    public const string HistoryPromptRatio = "history-prompt-ratio";

    /// <summary>Recorded operations of the same CLI and model without a prompt size: their mean per operation.</summary>
    public const string HistoryPerOperation = "history-per-operation";

    /// <summary>No matching history: rendered prompt characters / 4 as input and a 20% output ratio.</summary>
    public const string PromptSize = "prompt-size";
}

/// <summary>
/// Predicts a review run's tokens and cost. Only history of the same CLI and model is evidence:
/// another CLI or model caches, reads files, and answers differently, and borrowing its ratios is
/// what made a Claude preflight predict five times the actual cost. Operations recorded without
/// cache writes (<see cref="UsagePriceAccuracy.UnderPriced"/>) are not evidence either.
/// </summary>
public static class ReviewEstimator
{
    /// <summary>The most recent matching operations an estimate learns from.</summary>
    public const int MaxHistorySamples = 50;

    private const decimal FallbackOutputRatio = 0.20m;
    private const decimal CharactersPerToken = 4m;

    public static ReviewRunEstimate Estimate(
        IReadOnlyList<int> promptCharacters,
        IEnumerable<ReviewUsageEntry> history,
        string cliType,
        string? model,
        ModelPriceCatalog prices,
        DateTimeOffset now,
        int files,
        int expectedFreshSkips)
    {
        ArgumentNullException.ThrowIfNull(promptCharacters);
        ArgumentNullException.ThrowIfNull(history);
        var pricedModel = model ?? ReviewModelSource.RunnerDefault;
        var samples = history
            .Where(entry => string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(entry.Model, pricedModel, StringComparison.OrdinalIgnoreCase) &&
                            entry.PriceAccuracy != UsagePriceAccuracy.UnderPriced &&
                            entry.Tokens.InputTokens is > 0 && entry.Tokens.OutputTokens is >= 0)
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaxHistorySamples)
            .ToArray();
        var withPromptSize = samples.Where(entry => entry.PromptCharacters is > 0).ToArray();
        var totalCharacters = promptCharacters.Sum(characters => (long)characters);
        var route = $"{cliType}/{pricedModel}";

        PricingTokenUsage tokens;
        string basis, method;
        if (withPromptSize.Length > 0)
        {
            var sampleCharacters = withPromptSize.Sum(entry => (decimal)entry.PromptCharacters!.Value);
            var mix = Mix(withPromptSize);
            tokens = new PricingTokenUsage(
                promptCharacters.Sum(characters => Scale(characters, mix.Input / sampleCharacters)),
                promptCharacters.Sum(characters => Math.Max(1L, Scale(characters, mix.Output / sampleCharacters))),
                promptCharacters.Sum(characters => Scale(characters, mix.CacheRead / sampleCharacters)),
                promptCharacters.Sum(characters => Scale(characters, mix.CacheWrite / sampleCharacters)));
            basis = ReviewEstimateBasis.HistoryPromptRatio;
            method = $"Scaled from {withPromptSize.Length} recorded {route} operation(s) per prompt character, " +
                     "including their cache reads and writes.";
        }
        else if (samples.Length > 0)
        {
            var mix = Mix(samples);
            var operations = (decimal)promptCharacters.Count;
            tokens = new PricingTokenUsage(
                PerOperation(mix.Input, samples.Length, operations),
                Math.Max(promptCharacters.Count, PerOperation(mix.Output, samples.Length, operations)),
                PerOperation(mix.CacheRead, samples.Length, operations),
                PerOperation(mix.CacheWrite, samples.Length, operations));
            basis = ReviewEstimateBasis.HistoryPerOperation;
            method = $"Mean of {samples.Length} recorded {route} operation(s) per operation, including their cache " +
                     "reads and writes; they predate prompt-size recording, so prompt size does not scale it.";
        }
        else
        {
            tokens = new PricingTokenUsage(
                promptCharacters.Sum(characters => Scale(characters, 1m / CharactersPerToken)),
                promptCharacters.Sum(characters =>
                    Math.Max(1L, Scale(characters, FallbackOutputRatio / CharactersPerToken))),
                0, 0);
            basis = ReviewEstimateBasis.PromptSize;
            method = $"No recorded {route} operations: input is rendered prompt characters / 4 without caching, " +
                     "output a 20% fallback ratio.";
        }

        var cost = prices.ComputeCost(pricedModel, tokens, now.UtcDateTime);
        var status = cost.Status.ToString();
        return new ReviewRunEstimate(files, promptCharacters.Count, totalCharacters,
            tokens.Input + tokens.CacheRead + tokens.CacheWrite, tokens.Output,
            cost.Total, cost.Currency, char.ToLowerInvariant(status[0]) + status[1..], samples.Length, method,
            expectedFreshSkips, tokens.CacheRead, tokens.CacheWrite, basis);
    }

    private static (decimal Input, decimal CacheRead, decimal CacheWrite, decimal Output) Mix(
        IEnumerable<ReviewUsageEntry> samples)
    {
        decimal input = 0, cacheRead = 0, cacheWrite = 0, output = 0;
        foreach (var sample in samples)
        {
            var split = UsageLedger.ToPricingUsage(sample.Tokens);
            input += split.Input;
            cacheRead += split.CacheRead;
            cacheWrite += split.CacheWrite;
            output += split.Output;
        }
        return (input, cacheRead, cacheWrite, output);
    }

    private static long Scale(int characters, decimal ratio) => (long)Math.Ceiling(characters * ratio);

    private static long PerOperation(decimal total, int samples, decimal operations) =>
        (long)Math.Ceiling(total / samples * operations);
}
