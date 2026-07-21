using CodingAgentRunner;
using CodingAgentRunner.Model;
using CodingAgentRunner.Pricing;

namespace QualityStudio.Api;

/// <summary>One selectable CLI, shaped for the frontend's <c>ChatCliOption</c> contract.</summary>
public sealed record ModelCliOptionResponse(string Id, string Label, string? Icon);

/// <summary>One model entry, shaped for the frontend's <c>ChatModelOption</c> contract.</summary>
public sealed record ModelOptionResponse(
    string Id,
    string Label,
    bool Available,
    IReadOnlyList<string> ThinkingLevels,
    string? DefaultThinkingLevel);

/// <summary>
/// Builds the CLI + model catalog the review-start picker renders. QS keeps no model list of
/// its own: CLI identifiers come from <see cref="CliTypes"/>, model ids/vendors come from
/// <see cref="ModelPriceCatalog.Default"/>, and per-model reasoning levels come from each
/// driver's <see cref="CodingAgentRunner.Execution.ICliDriver.Capabilities"/> — all owned by the
/// CodingAgentRunner package QS already depends on for execution. See frontend/README.md for how
/// an update to that package (or a coding-agent-chat rebuild) reaches this catalog.
/// </summary>
public sealed class ModelCatalogService
{
    // Keyed by the raw CLI type strings rather than CliTypes.Gemini, which is [Obsolete] on the
    // member (not the value) — Gemini stays a selectable, labelled CLI while it remains in
    // CliTypes.All, deprecation is the runner's call to make, not this catalog's.
    private static readonly IReadOnlyDictionary<string, string> DisplayLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CliTypes.Claude] = "Claude Code",
        [CliTypes.Codex] = "Codex",
        ["gemini"] = "Gemini CLI",
    };

    private static readonly IReadOnlyDictionary<string, string> DisplayIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CliTypes.Claude] = "✳",
        [CliTypes.Codex] = "◆",
        ["gemini"] = "✦",
    };

    /// <summary>Maps a CLI type to the <c>ModelListing.Vendor</c> grouping its models are seeded under.</summary>
    private static readonly IReadOnlyDictionary<string, string> VendorByCliType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [CliTypes.Claude] = "anthropic",
        [CliTypes.Codex] = "openai",
        ["gemini"] = "google",
    };

    private readonly CliRunner runner = new();

    /// <summary>The selectable CLIs, in <see cref="CliTypes.All"/> order.</summary>
    public IReadOnlyList<ModelCliOptionResponse> CliOptions() => CliTypes.All
        .Select(type => new ModelCliOptionResponse(type, DisplayLabels.GetValueOrDefault(type, type), DisplayIcons.GetValueOrDefault(type)))
        .ToArray();

    /// <summary>
    /// The model catalog for one CLI. Empty when the CLI's vendor has no priced/known listings
    /// yet (e.g. Gemini/Google) — never a guessed or hardcoded fallback list.
    /// </summary>
    public IReadOnlyList<ModelOptionResponse> Models(string cliType)
    {
        if (!CliTypes.IsValid(cliType))
        {
            throw new ArgumentException($"Unknown CLI type '{cliType}'. Known: {string.Join(", ", CliTypes.All)}.", nameof(cliType));
        }

        if (!VendorByCliType.TryGetValue(cliType, out var vendor))
        {
            return [];
        }

        var driver = runner.Get(cliType);
        return ModelPriceCatalog.Default.Listings
            .Where(listing => string.Equals(listing.Vendor, vendor, StringComparison.OrdinalIgnoreCase))
            .Select(listing =>
            {
                var capabilities = driver.Capabilities(listing.ModelId);
                return new ModelOptionResponse(listing.ModelId, listing.ModelId, Available: true,
                    capabilities.ThinkingLevels, capabilities.DefaultThinkingLevel);
            })
            .ToArray();
    }
}
