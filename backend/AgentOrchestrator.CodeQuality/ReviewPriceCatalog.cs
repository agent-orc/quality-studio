using System.Reflection;
using System.Text.Json;
using CodingAgentRunner.Pricing;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The price catalog Quality Studio computes costs with: the synchronized Token Economy price
/// snapshot embedded next to the routing policy. CodingAgentRunner's own seed catalog does not list
/// the policy's route models (gpt-5.6-luna, -terra, -sol), so pricing through it reported every
/// policy route as an unknown model and no run could be costed.
/// </summary>
public static class ReviewPriceCatalog
{
    private const string PricesResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-prices.json";

    public static ModelPriceCatalog Default { get; } = Load();

    private static ModelPriceCatalog Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PricesResource)
            ?? throw new InvalidOperationException($"Embedded price catalog resource '{PricesResource}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var listings = document.RootElement.EnumerateArray().Select(model => new ModelListing
        {
            ModelId = model.GetProperty("modelId").GetString()!,
            Aliases = model.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array
                ? aliases.EnumerateArray().Select(alias => alias.GetString()!).ToArray()
                : [],
            Vendor = model.TryGetProperty("vendor", out var vendor) ? vendor.GetString() : null,
            History = model.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array
                ? history.EnumerateArray().Select(ReadPrice).ToArray()
                : [],
        }).ToArray();
        return new ModelPriceCatalog(listings);
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

    private static decimal? OptionalDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;
}
