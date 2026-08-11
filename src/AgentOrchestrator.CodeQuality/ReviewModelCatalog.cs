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

/// <summary>A server-owned route recommendation derived from the synchronized routing policy.</summary>
public sealed record ReviewModelRecommendation(
    string PolicyVersion,
    string RecommendedModel,
    string RecommendedThinkingLevel,
    string CapabilityTier,
    int Score,
    string CorrectnessFloor,
    string Reason,
    string SelectionSource);

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

    /// <summary>
    /// Applies the policy's core-task score bands and hard floors. Quota and price deliberately
    /// contribute no downward adjustment: they may influence an operator override, never the floor.
    /// </summary>
    public ReviewModelRecommendation Recommend(string kind, ReviewLevel level, int files)
    {
        if (files <= 0) throw new ArgumentOutOfRangeException(nameof(files));
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var aggregate = level is ReviewLevel.Project or ReviewLevel.Module or ReviewLevel.Namespace;
        var score = (normalizedKind == "security" ? 35 : normalizedKind == "performance" ? 16 : 10)
                    + (files == 1 ? 4 : files <= 10 ? 10 : files <= 50 ? 15 : 20)
                    + (aggregate ? 16 : 4)
                    + (normalizedKind == "security" ? 10 : aggregate ? 8 : 5)
                    + 5;
        score = Math.Clamp(score, 0, 100);

        var scoredRoute = score switch
        {
            <= 20 => (Id: "luna-medium", Model: "gpt-5.6-luna", Thinking: "medium", Tier: "light", Rank: 0),
            <= 50 => (Id: "terra-medium", Model: "gpt-5.6-terra", Thinking: "medium", Tier: "balanced", Rank: 1),
            <= 69 => (Id: "sol-medium", Model: "gpt-5.6-sol", Thinking: "medium", Tier: "frontier", Rank: 2),
            _ => (Id: "sol-xhigh", Model: "gpt-5.6-sol", Thinking: "xhigh", Tier: "frontier", Rank: 3),
        };
        var floor = normalizedKind == "security"
            ? (Id: "sol-xhigh", Model: "gpt-5.6-sol", Thinking: "xhigh", Tier: "frontier", Rank: 3)
            : aggregate || files > 50
                ? (Id: "sol-medium", Model: "gpt-5.6-sol", Thinking: "medium", Tier: "frontier", Rank: 2)
                : (Id: "luna-medium", Model: "gpt-5.6-luna", Thinking: "medium", Tier: "light", Rank: 0);
        var route = scoredRoute.Rank >= floor.Rank ? scoredRoute : floor;
        var scopeReason = aggregate
            ? $"{level.ToString().ToLowerInvariant()} scope across {files} files"
            : $"{files} file{(files == 1 ? string.Empty : "s")}";
        var floorReason = floor.Rank > scoredRoute.Rank ? $" The {floor.Id} correctness floor raises the scored route." : string.Empty;
        return new ReviewModelRecommendation(
            Snapshot.PolicyVersion,
            route.Model,
            route.Thinking,
            route.Tier,
            score,
            floor.Id,
            $"Policy score {score} for {normalizedKind} review at {scopeReason}.{floorReason} Price and quota do not lower this floor.",
            "model-routing-policy");
    }

    /// <summary>Returns true when an explicit route can be shown not to qualify at the hard floor.</summary>
    public bool IsBelowCorrectnessFloor(ReviewModelSelection selection, ReviewModelRecommendation recommendation)
    {
        if (selection.Model is null || selection.ThinkingLevel is null) return false;
        var floorRank = recommendation.CorrectnessFloor switch
        {
            "sol-xhigh" => 3,
            "sol-medium" => 2,
            "terra-medium" => 1,
            _ => 0,
        };
        var option = Find(selection.Model);
        if (option is null) return true;
        var thinkingRanks = Snapshot.ThinkingLevels.Select((level, rank) => (level, rank))
            .ToDictionary(item => item.level, item => item.rank, StringComparer.OrdinalIgnoreCase);
        var thinkingRank = selection.ThinkingLevel is null
            ? -1
            : thinkingRanks.GetValueOrDefault(selection.ThinkingLevel, -1);
        var selectedRank = option.ModelId switch
        {
            "gpt-5.6-sol" when thinkingRank >= thinkingRanks["xhigh"] => 3,
            "gpt-5.6-sol" when thinkingRank >= thinkingRanks["medium"] => 2,
            "gpt-5.6-terra" when thinkingRank >= thinkingRanks["medium"] => 1,
            "gpt-5.6-luna" when thinkingRank >= thinkingRanks["medium"] => 0,
            "claude-sonnet-5" when thinkingRank >= thinkingRanks["high"] => 2,
            _ => -1,
        };
        return selectedRank < floorRank;
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
