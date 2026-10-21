using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodingAgentRunner.Pricing;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The price catalog Quality Studio computes costs with: the synchronized Token Economy price
/// snapshot embedded next to the routing policy. CodingAgentRunner's own seed catalog does not list
/// the policy's route models (gpt-5.6-luna, -terra, -sol), so pricing through it reported every
/// policy route as an unknown model and no run could be costed.
/// </summary>
public static class ReviewPriceCatalog
{
    /// <summary>
    /// Token Economy's status for a prompt-size tariff whose tier the usage cannot establish. The
    /// total stays null so the cheap tier is never reported as the cost.
    /// </summary>
    public const string AmbiguousPromptSize = "ambiguousPromptSize";

    private const string PricesResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-prices.json";

    // CodingAgentRunner's ModelPrice has no prompt-size tiers (Token Economy 0.3.8 added them), so
    // the tiers of each catalog loaded here are kept beside it, keyed by model and price validFrom.
    private static readonly ConditionalWeakTable<ModelPriceCatalog, Dictionary<(string ModelId, DateTime ValidFrom), PromptSizeTier[]>> Tiers = new();

    public static ModelPriceCatalog Default { get; } = Load();

    /// <summary>A whole-request tariff that applies once one request's prompt reaches <see cref="MinPromptTokens"/>.</summary>
    private sealed record PromptSizeTier(long MinPromptTokens, ModelPrice Price);

    /// <summary>
    /// Prices one operation at the catalog price valid at <paramref name="atUtc"/>. An operation is a
    /// CLI run that can span several model requests, and only its summed usage is known, while a
    /// prompt-size tariff bills each request by that request's own prompt. A sum below the first tier
    /// proves every request was below it too; any larger sum, or an unknown input count, leaves the
    /// tier open, so the cost is <see cref="AmbiguousPromptSize"/> with the base-tier and the
    /// highest-reachable-tier totals as its bounds.
    /// </summary>
    public static UsageCost Price(ModelPriceCatalog catalog, string model, PricingTokenUsage usage, bool inputKnown, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var cost = catalog.ComputeCost(model, usage, atUtc);
        var status = cost.Status.ToString();
        var priced = new UsageCost(cost.Total, cost.Currency, char.ToLowerInvariant(status[0]) + status[1..]);
        if (cost.Status != PriceStatus.Resolved || cost.Price is null || cost.ModelId is null ||
            !Tiers.TryGetValue(catalog, out var byPrice) ||
            !byPrice.TryGetValue((cost.ModelId, cost.Price.ValidFrom), out var tiers))
            return priced;

        if (!inputKnown) return new UsageCost(null, cost.Currency, AmbiguousPromptSize);
        var promptTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        if (promptTokens < tiers[0].MinPromptTokens) return priced;
        var reachable = tiers.Last(tier => tier.MinPromptTokens <= promptTokens);
        var tariff = new ModelPriceCatalog([new ModelListing { ModelId = cost.ModelId, History = [reachable.Price] }]);
        return new UsageCost(null, cost.Currency, AmbiguousPromptSize,
            cost.Total, tariff.ComputeCost(cost.ModelId, usage, atUtc).Total);
    }

    private static ModelPriceCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PricesResource)
            ?? throw new InvalidOperationException($"Embedded price catalog resource '{PricesResource}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var tiers = new Dictionary<(string ModelId, DateTime ValidFrom), PromptSizeTier[]>();
        var listings = document.RootElement.EnumerateArray().Select(model =>
        {
            var modelId = model.GetProperty("modelId").GetString()!;
            var history = new List<ModelPrice>();
            if (model.TryGetProperty("history", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var price = ReadPrice(entry);
                    history.Add(price);
                    if (ReadTiers(modelId, entry, price) is { Length: > 0 } entryTiers)
                        tiers[(modelId, price.ValidFrom)] = entryTiers;
                }
            }
            return new ModelListing
            {
                ModelId = modelId,
                Aliases = model.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array
                    ? aliases.EnumerateArray().Select(alias => alias.GetString()!).ToArray()
                    : [],
                Vendor = model.TryGetProperty("vendor", out var vendor) ? vendor.GetString() : null,
                History = history,
            };
        }).ToArray();
        var catalog = new ModelPriceCatalog(listings);
        Tiers.Add(catalog, tiers);
        return catalog;
    }

    private static ModelPrice ReadPrice(JsonElement price) => new()
    {
        InputPerMTok = price.GetProperty("inputPerMTok").GetDecimal(),
        OutputPerMTok = price.GetProperty("outputPerMTok").GetDecimal(),
        CacheReadPerMTok = OptionalDecimal(price, "cacheReadPerMTok"),
        CacheWritePerMTok = OptionalDecimal(price, "cacheWritePerMTok"),
        Currency = price.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String
            ? currency.GetString()!
            : Currencies.Usd,
        // The snapshot carries validTo for readability; the catalog resolves by validFrom, where a
        // later entry supersedes the earlier one, so validTo needs no separate mapping.
        ValidFrom = price.TryGetProperty("validFrom", out var validFrom) && validFrom.ValueKind == JsonValueKind.String
            ? DateTime.SpecifyKind(validFrom.GetDateTime().ToUniversalTime(), DateTimeKind.Utc)
            : DateTime.MinValue,
        Source = price.TryGetProperty("source", out var source) ? source.GetString() : null,
        Note = price.TryGetProperty("note", out var note) ? note.GetString() : null,
        Unconfirmed = price.TryGetProperty("unconfirmed", out var unconfirmed) && unconfirmed.ValueKind == JsonValueKind.True,
    };

    /// <summary>
    /// Reads <c>promptSizeTiers</c> as full prices that inherit the entry's currency and validity. A
    /// cache rate the tier omits falls back to the entry's own cache rate, then to the tier's input
    /// rate, as in Token Economy, which also requires tiers to start above zero and be distinct.
    /// </summary>
    private static PromptSizeTier[] ReadTiers(string modelId, JsonElement entry, ModelPrice basePrice)
    {
        if (!entry.TryGetProperty("promptSizeTiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) return [];
        var read = tiers.EnumerateArray().Select(tier => new PromptSizeTier(
            tier.GetProperty("minPromptTokens").GetInt64(),
            basePrice with
            {
                InputPerMTok = tier.GetProperty("inputPerMTok").GetDecimal(),
                OutputPerMTok = tier.GetProperty("outputPerMTok").GetDecimal(),
                CacheReadPerMTok = OptionalDecimal(tier, "cacheReadPerMTok") ?? basePrice.CacheReadPerMTok,
                CacheWritePerMTok = OptionalDecimal(tier, "cacheWritePerMTok") ?? basePrice.CacheWritePerMTok,
            })).OrderBy(tier => tier.MinPromptTokens).ToArray();
        if (read.Any(tier => tier.MinPromptTokens <= 0) ||
            read.Select(tier => tier.MinPromptTokens).Distinct().Count() != read.Length)
            throw new InvalidOperationException($"Price catalog model '{modelId}' declares invalid prompt-size tiers.");
        return read;
    }

    private static decimal? OptionalDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}
