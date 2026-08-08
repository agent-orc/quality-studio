using System.Reflection;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>A model option governed by the synchronized Token Economy routing and price catalogs.</summary>
public sealed record ReviewModelOption(
    string ModelId,
    IReadOnlyList<string> Aliases,
    string CliType,
    string CapabilityTier,
    string Suitability,
    string RoutingStatus,
    IReadOnlyList<string> SupportedThinkingLevels,
    bool Provisional,
    string EvidenceStatus,
    string Note,
    bool PriceAvailable,
    bool AvailableForNewRuns);

/// <summary>The picker contract plus exact provenance of its synchronized Token Economy snapshot.</summary>
public sealed record ReviewModelCatalogSnapshot(
    int SchemaVersion,
    string PolicyVersion,
    string EvidenceAsOfDate,
    string SourceRepository,
    string SourceCommit,
    IReadOnlyList<string> ThinkingLevels,
    IReadOnlyList<ReviewModelOption> Models);

/// <summary>A normalized review route ready to persist and pass to CodingAgentRunner.</summary>
public sealed record ReviewModelSelection(string CliType, string? Model, string? ThinkingLevel, bool Catalogued);

/// <summary>
/// Reads the governed Token Economy snapshot embedded in Quality Studio. Catalogued retired,
/// restricted, and unsupported models are rejected; a CLI-family-compatible custom id remains
/// available as the deliberate forward-compatibility escape hatch.
/// </summary>
public sealed class ReviewModelCatalog
{
    private const string RoutingResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-routing-policy.json";
    private const string PricesResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-prices.json";
    private const string SnapshotResource =
        "AgentOrchestrator.CodeQuality.catalogues.token-economy-model-catalog.snapshot.json";
    private static readonly HashSet<string> NewRunStatuses = ["selectable", "fallbackOnly"];
    private static readonly HashSet<string> KnownCliTypes = ["codex", "claude", "gemini", "antigravity"];
    private readonly Dictionary<string, ReviewModelOption> modelsByKey;

    public ReviewModelCatalog()
    {
        Snapshot = Load();
        modelsByKey = new Dictionary<string, ReviewModelOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Snapshot.Models)
        {
            Add(model.ModelId, model);
            foreach (var alias in model.Aliases) Add(alias, model);
        }
    }

    public static ReviewModelCatalog Default { get; } = new();

    public ReviewModelCatalogSnapshot Snapshot { get; }

    public ReviewModelOption? Find(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : modelsByKey.GetValueOrDefault(model.Trim());

    public ReviewModelSelection Resolve(string? cliType, string? model, string? thinkingLevel)
    {
        var cli = NormalizeCli(cliType);
        var requestedModel = Text(model);
        var requestedThinking = Text(thinkingLevel);
        if (requestedModel is null)
        {
            if (requestedThinking is not null)
                throw new ArgumentException("A thinking-level override requires a model override.");
            return new ReviewModelSelection(cli, null, null, false);
        }

        RequireSafeIdentifier(requestedModel, "Model");
        if (requestedThinking is not null) RequireSafeIdentifier(requestedThinking, "Thinking level");

        var catalogued = Find(requestedModel);
        if (catalogued is null)
        {
            if (KnownCliTypes.Contains(cli) && !HasCliPrefix(cli, requestedModel))
                throw new ArgumentException($"Model '{requestedModel}' is not compatible with CLI '{cli}'.");
            return new ReviewModelSelection(cli, requestedModel, requestedThinking, false);
        }

        if (!catalogued.AvailableForNewRuns)
            throw new ArgumentException(
                $"Model '{catalogued.ModelId}' cannot start new reviews because its routing status is '{catalogued.RoutingStatus}'.");
        if (KnownCliTypes.Contains(cli) && !string.Equals(catalogued.CliType, cli, StringComparison.Ordinal))
            throw new ArgumentException($"Model '{catalogued.ModelId}' is routed through CLI '{catalogued.CliType}', not '{cli}'.");
        if (requestedThinking is not null &&
            !catalogued.SupportedThinkingLevels.Contains(requestedThinking, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Model '{catalogued.ModelId}' does not support thinking level '{requestedThinking}'.");

        var canonicalThinking = requestedThinking is null
            ? null
            : catalogued.SupportedThinkingLevels.First(level =>
                string.Equals(level, requestedThinking, StringComparison.OrdinalIgnoreCase));
        return new ReviewModelSelection(cli, catalogued.ModelId, canonicalThinking, true);
    }

    public static string NormalizeCli(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return "codex";
        var normalized = cliType.Trim().ToLowerInvariant();
        return normalized == "claude-code" ? "claude" : normalized;
    }

    private static ReviewModelCatalogSnapshot Load()
    {
        using var routing = JsonDocument.Parse(OpenResource(RoutingResource));
        using var prices = JsonDocument.Parse(OpenResource(PricesResource));
        using var snapshot = JsonDocument.Parse(OpenResource(SnapshotResource));

        var routingRoot = routing.RootElement;
        var snapshotRoot = snapshot.RootElement;
        var pricedModels = prices.RootElement.EnumerateArray().ToDictionary(
            item => item.GetProperty("modelId").GetString()!,
            item => item.TryGetProperty("history", out var history) && history.GetArrayLength() > 0,
            StringComparer.Ordinal);
        var thinkingLevels = routingRoot.GetProperty("thinkingLevels").EnumerateArray()
            .OrderBy(level => level.GetProperty("rank").GetInt32())
            .Select(level => level.GetProperty("id").GetString()!)
            .ToArray();
        var models = routingRoot.GetProperty("models").EnumerateArray().Select(model =>
        {
            var modelId = model.GetProperty("canonicalId").GetString()!;
            var routingStatus = model.GetProperty("routingStatus").GetString()!;
            var tier = model.GetProperty("capabilityTier").GetString()!;
            var roles = Strings(model.GetProperty("workflowRoles"));
            return new ReviewModelOption(
                modelId,
                Strings(model.GetProperty("aliases")),
                NormalizeCli(model.GetProperty("cliId").GetString()),
                tier,
                Suitability(tier, roles, routingStatus),
                routingStatus,
                Strings(model.GetProperty("supportedThinkingLevels")),
                model.GetProperty("provisional").GetBoolean(),
                model.GetProperty("evidenceStatus").GetString()!,
                model.GetProperty("note").GetString()!,
                pricedModels.GetValueOrDefault(model.GetProperty("priceCatalogId").GetString()!),
                NewRunStatuses.Contains(routingStatus));
        }).ToArray();

        return new ReviewModelCatalogSnapshot(
            snapshotRoot.GetProperty("schemaVersion").GetInt32(),
            routingRoot.GetProperty("policyVersion").GetString()!,
            routingRoot.GetProperty("evidenceAsOfDate").GetString()!,
            snapshotRoot.GetProperty("upstreamRepository").GetString()!,
            snapshotRoot.GetProperty("upstreamCommit").GetString()!,
            thinkingLevels,
            models);
    }

    private static string Suitability(string tier, IReadOnlyList<string> roles, string routingStatus)
    {
        if (!roles.Contains("coreTask", StringComparer.Ordinal))
            return "Bounded pipeline decisions only; not qualified for core code reviews.";
        var baseline = tier switch
        {
            "light" => "Small, well-specified reviews with an obvious verification path.",
            "balanced" => "Standard reversible reviews with clear scope and test seams.",
            "frontier" => "Demanding or correctness-critical reviews with broad context.",
            _ => "No capability guidance is available.",
        };
        return routingStatus == "fallbackOnly" ? $"Equivalent-provider fallback only. {baseline}" : baseline;
    }

    private static IReadOnlyList<string> Strings(JsonElement value) =>
        value.EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"Embedded model catalog resource '{name}' was not found.");

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HasCliPrefix(string cliType, string model) => cliType switch
    {
        "codex" => model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase),
        "claude" => model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase),
        "gemini" or "antigravity" => model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static void RequireSafeIdentifier(string value, string label)
    {
        if (value.Length > 100 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
            throw new ArgumentException($"{label} contains unsupported characters.");
    }

    private void Add(string key, ReviewModelOption model)
    {
        if (!modelsByKey.TryAdd(key, model))
            throw new InvalidOperationException($"Duplicate model catalog key '{key}'.");
    }
}
