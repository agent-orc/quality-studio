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
/// How a run's model was chosen. Persisted with the run manifest, the run response, and every
/// usage-ledger entry so cost and quality evidence can always be attributed to a real model id.
/// </summary>
public static class ReviewModelSource
{
    /// <summary>The caller named the model.</summary>
    public const string Explicit = "explicit";

    /// <summary>
    /// The caller named no model; Quality Studio resolved the synchronized routing policy's route
    /// for the CLI and passed that model to the CLI explicitly.
    /// </summary>
    public const string PolicyDefault = "policy-default";

    /// <summary>
    /// The caller named no model and the policy routes nothing for the CLI, so the CLI's own
    /// configured default served the run. Quality Studio does not know that model and records the
    /// literal "runner-default" instead of guessing.
    /// </summary>
    public const string RunnerDefault = "runner-default";
}

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
/// A rejected model or thinking-level override. Distinct from a plain <see cref="ArgumentException"/>
/// so the API can name the model as the cause instead of reporting a path problem, and because the
/// message is built only from catalog ids and validated identifiers it is safe to show the operator.
/// </summary>
public sealed class ReviewModelSelectionException(string message) : ArgumentException(message);

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
    private const string LunaMediumRoute = "luna-medium";
    private const string TerraMediumRoute = "terra-medium";
    private const string SolMediumRoute = "sol-medium";
    private const string SolXhighRoute = "sol-xhigh";

    /// <summary>
    /// The core-task routes <see cref="Recommend"/> can name. The synchronized policy must define
    /// every one of them: a floor whose route the policy no longer declares would otherwise rank at
    /// zero and silently disable the gate, so the mismatch is a load-time failure instead.
    /// </summary>
    private static readonly string[] RecommendableRoutes =
        [LunaMediumRoute, TerraMediumRoute, SolMediumRoute, SolXhighRoute];

    private static readonly HashSet<string> NewRunStatuses = ["selectable", "fallbackOnly"];
    private static readonly HashSet<string> KnownCliTypes = ["codex", "claude", "gemini", "antigravity"];
    private readonly Dictionary<string, ReviewModelOption> modelsByKey;
    private readonly Dictionary<string, int> thinkingRanks;
    private readonly Dictionary<string, int> coreRouteRanks;
    private readonly IReadOnlyList<QualifyingRoute> qualifyingRoutes;
    private readonly IReadOnlyDictionary<string, (string Model, string ThinkingLevel)> coreRoutes;
    private readonly IReadOnlyList<ProviderFallback> providerFallbacks;

    public ReviewModelCatalog()
    {
        (Snapshot, thinkingRanks, coreRouteRanks, qualifyingRoutes, coreRoutes, providerFallbacks) = Load();
        modelsByKey = new Dictionary<string, ReviewModelOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in Snapshot.Models)
        {
            Add(model.ModelId, model);
            foreach (var alias in model.Aliases) Add(alias, model);
        }
    }

    /// <summary>A (model, minimum thinking level) pair the policy qualifies at a core-task route rank.</summary>
    private sealed record QualifyingRoute(string ModelId, int MinimumThinkingRank, int Rank);

    /// <summary>An equivalent-provider fallback the policy declares for specific core-task routes.</summary>
    private sealed record ProviderFallback(
        string Id,
        string ModelId,
        string ThinkingLevel,
        IReadOnlyList<string> ForRouteIds,
        IReadOnlyList<string> NotForRouteIds);

    public static ReviewModelCatalog Default { get; } = new();

    public ReviewModelCatalogSnapshot Snapshot { get; }

    public ReviewModelOption? Find(string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : modelsByKey.GetValueOrDefault(model.Trim());

    public ReviewModelSelection Resolve(string? cliType, string? model, string? thinkingLevel)
    {
        var cli = NormalizeCli(cliType);
        // The CLI reaches rejection messages that the API echoes to the caller, so it is held to the
        // same identifier rule as the model and thinking level rather than being reflected verbatim.
        RequireSafeIdentifier(cli, "CLI type");
        var requestedModel = Text(model);
        var requestedThinking = Text(thinkingLevel);
        if (requestedModel is null)
        {
            if (requestedThinking is not null)
                throw new ReviewModelSelectionException("A thinking-level override requires a model override.");
            return new ReviewModelSelection(cli, null, null, false);
        }

        RequireSafeIdentifier(requestedModel, "Model");
        if (requestedThinking is not null) RequireSafeIdentifier(requestedThinking, "Thinking level");

        var catalogued = Find(requestedModel);
        if (catalogued is null)
        {
            if (KnownCliTypes.Contains(cli) && !HasCliPrefix(cli, requestedModel))
                throw new ReviewModelSelectionException($"Model '{requestedModel}' is not compatible with CLI '{cli}'.");
            return new ReviewModelSelection(cli, requestedModel, requestedThinking, false);
        }

        if (!catalogued.AvailableForNewRuns)
            throw new ReviewModelSelectionException(
                $"Model '{catalogued.ModelId}' cannot start new reviews because its routing status is '{catalogued.RoutingStatus}'.");
        if (KnownCliTypes.Contains(cli) && !string.Equals(catalogued.CliType, cli, StringComparison.Ordinal))
            throw new ReviewModelSelectionException($"Model '{catalogued.ModelId}' is routed through CLI '{catalogued.CliType}', not '{cli}'.");
        if (requestedThinking is not null &&
            !catalogued.SupportedThinkingLevels.Contains(requestedThinking, StringComparer.OrdinalIgnoreCase))
            throw new ReviewModelSelectionException(
                $"Model '{catalogued.ModelId}' does not support thinking level '{requestedThinking}'.");

        var canonicalThinking = requestedThinking is null
            ? null
            : catalogued.SupportedThinkingLevels.First(level =>
                string.Equals(level, requestedThinking, StringComparison.OrdinalIgnoreCase));
        return new ReviewModelSelection(cli, catalogued.ModelId, canonicalThinking, true);
    }

    /// <summary>
    /// Resolves the route a run uses when the caller named no model, so the run, its sidecars, and
    /// its ledger entries carry a real model id. Codex runs the policy recommendation. Claude runs
    /// the policy's provider fallback for the recommended route; when the policy withholds every
    /// fallback from that route (a security floor, for example) it runs the strongest
    /// fallback-eligible claude model and the recommendation still names the floor that model does
    /// not reach. A CLI the policy does not route returns null: the CLI's own default serves the run
    /// and callers record <see cref="ReviewModelSource.RunnerDefault"/> instead of a guess.
    /// </summary>
    public ReviewModelSelection? ResolveDefault(string? cliType, ReviewModelRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        var cli = NormalizeCli(cliType);
        if (cli == "codex")
        {
            var recommended = Find(recommendation.RecommendedModel);
            return recommended is { AvailableForNewRuns: true, CliType: "codex" }
                ? new ReviewModelSelection(cli, recommended.ModelId, recommendation.RecommendedThinkingLevel, true)
                : null;
        }

        var routeId = coreRoutes.FirstOrDefault(route =>
            string.Equals(route.Value.Model, recommendation.RecommendedModel, StringComparison.Ordinal) &&
            string.Equals(route.Value.ThinkingLevel, recommendation.RecommendedThinkingLevel, StringComparison.OrdinalIgnoreCase)).Key;
        var eligible = providerFallbacks
            .Select(fallback => (Fallback: fallback, Option: Find(fallback.ModelId)))
            .Where(candidate => candidate.Option is { AvailableForNewRuns: true } && candidate.Option.CliType == cli)
            .ToArray();
        var forRoute = routeId is null
            ? default
            : eligible.FirstOrDefault(candidate =>
                candidate.Fallback.ForRouteIds.Contains(routeId, StringComparer.Ordinal) &&
                !candidate.Fallback.NotForRouteIds.Contains(routeId, StringComparer.Ordinal));
        var chosen = forRoute.Option is not null
            ? forRoute
            : eligible
                .OrderByDescending(candidate => candidate.Fallback.ForRouteIds
                    .Select(id => coreRouteRanks.GetValueOrDefault(id, -1))
                    .DefaultIfEmpty(-1)
                    .Max())
                .ThenBy(candidate => candidate.Option!.ModelId, StringComparer.Ordinal)
                .FirstOrDefault();
        if (chosen.Option is null) return null;

        var thinking = chosen.Option.SupportedThinkingLevels.FirstOrDefault(level =>
                           string.Equals(level, chosen.Fallback.ThinkingLevel, StringComparison.OrdinalIgnoreCase))
                       ?? chosen.Option.SupportedThinkingLevels[chosen.Option.SupportedThinkingLevels.Count - 1];
        return new ReviewModelSelection(cli, chosen.Option.ModelId, thinking, true);
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
            <= 20 => (Id: LunaMediumRoute, Model: "gpt-5.6-luna", Thinking: "medium", Tier: "light"),
            <= 50 => (Id: TerraMediumRoute, Model: "gpt-5.6-terra", Thinking: "medium", Tier: "balanced"),
            <= 69 => (Id: SolMediumRoute, Model: "gpt-5.6-sol", Thinking: "medium", Tier: "frontier"),
            _ => (Id: SolXhighRoute, Model: "gpt-5.6-sol", Thinking: "xhigh", Tier: "frontier"),
        };
        var floor = normalizedKind == "security"
            ? (Id: SolXhighRoute, Model: "gpt-5.6-sol", Thinking: "xhigh", Tier: "frontier")
            : aggregate || files > 50
                ? (Id: SolMediumRoute, Model: "gpt-5.6-sol", Thinking: "medium", Tier: "frontier")
                : (Id: LunaMediumRoute, Model: "gpt-5.6-luna", Thinking: "medium", Tier: "light");
        var scoredRank = coreRouteRanks[scoredRoute.Id];
        var floorRank = coreRouteRanks[floor.Id];
        var route = scoredRank >= floorRank ? scoredRoute : floor;
        var scopeReason = aggregate
            ? $"{level.ToString().ToLowerInvariant()} scope across {files} files"
            : $"{files} file{(files == 1 ? string.Empty : "s")}";
        var floorReason = floorRank > scoredRank ? $" The {floor.Id} correctness floor raises the scored route." : string.Empty;
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

    /// <summary>
    /// Returns true when an explicit route can be shown not to qualify at the hard floor. The
    /// model-to-rank ladder is read from the policy's own core-task routes and provider fallbacks,
    /// so promoting a model upstream is a catalog sync here rather than a code change. A model the
    /// policy never qualifies for a core-task route stays below every floor above the lightest one.
    /// </summary>
    public bool IsBelowCorrectnessFloor(ReviewModelSelection selection, ReviewModelRecommendation recommendation)
    {
        if (selection.Model is null || selection.ThinkingLevel is null) return false;
        // A floor the policy does not declare is a caller error, not a licence to skip the gate.
        if (recommendation.CorrectnessFloor is null ||
            !coreRouteRanks.TryGetValue(recommendation.CorrectnessFloor, out var floorRank)) return true;
        var option = Find(selection.Model);
        if (option is null) return true;
        var thinkingRank = thinkingRanks.GetValueOrDefault(selection.ThinkingLevel, -1);
        var selectedRank = qualifyingRoutes
            .Where(route => string.Equals(route.ModelId, option.ModelId, StringComparison.Ordinal)
                            && thinkingRank >= route.MinimumThinkingRank)
            .Select(route => (int?)route.Rank)
            .Max() ?? -1;
        return selectedRank < floorRank;
    }

    public static string NormalizeCli(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return "codex";
        var normalized = cliType.Trim().ToLowerInvariant();
        return normalized == "claude-code" ? "claude" : normalized;
    }

    private static (ReviewModelCatalogSnapshot Snapshot,
        Dictionary<string, int> ThinkingRanks,
        Dictionary<string, int> CoreRouteRanks,
        IReadOnlyList<QualifyingRoute> QualifyingRoutes,
        IReadOnlyDictionary<string, (string Model, string ThinkingLevel)> CoreRoutes,
        IReadOnlyList<ProviderFallback> ProviderFallbacks) Load()
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

        var thinkingRanks = thinkingLevels
            .Select((level, rank) => (level, rank))
            .ToDictionary(item => item.level, item => item.rank, StringComparer.OrdinalIgnoreCase);
        var (coreRouteRanks, qualifyingRoutes, coreRoutes, providerFallbacks) = LoadFloorLadder(
            routingRoot, thinkingRanks, models.Select(model => model.ModelId).ToHashSet(StringComparer.Ordinal));

        return (new ReviewModelCatalogSnapshot(
                snapshotRoot.GetProperty("schemaVersion").GetInt32(),
                routingRoot.GetProperty("policyVersion").GetString()!,
                routingRoot.GetProperty("evidenceAsOfDate").GetString()!,
                snapshotRoot.GetProperty("upstreamRepository").GetString()!,
                snapshotRoot.GetProperty("upstreamCommit").GetString()!,
                thinkingLevels,
                models),
            thinkingRanks,
            coreRouteRanks,
            qualifyingRoutes,
            coreRoutes,
            providerFallbacks);
    }

    /// <summary>
    /// Projects the policy's core-task routes and provider fallbacks onto the rank ladder used by
    /// the floor comparison. A fallback inherits the strongest core-task route listed in its
    /// <c>forRouteIds</c>; a route it is not listed for, or is excluded from by
    /// <c>notForRouteIds</c>, contributes nothing, so an equivalent-provider fallback can never
    /// reach a floor the policy withheld from it. Bounded-pipeline routes are not a core-task
    /// ladder and are skipped entirely.
    /// </summary>
    private static (Dictionary<string, int> CoreRouteRanks,
        IReadOnlyList<QualifyingRoute> QualifyingRoutes,
        IReadOnlyDictionary<string, (string Model, string ThinkingLevel)> CoreRoutes,
        IReadOnlyList<ProviderFallback> ProviderFallbacks)
        LoadFloorLadder(JsonElement routingRoot, Dictionary<string, int> thinkingRanks, HashSet<string> knownModelIds)
    {
        var coreRouteRanks = new Dictionary<string, int>(StringComparer.Ordinal);
        var coreRoutes = new Dictionary<string, (string Model, string ThinkingLevel)>(StringComparer.Ordinal);
        var qualifying = new List<QualifyingRoute>();
        var providerFallbacks = new List<ProviderFallback>();

        foreach (var route in routingRoot.GetProperty("routes").EnumerateArray())
        {
            if (route.GetProperty("workflowRole").GetString() != "coreTask") continue;
            var id = route.GetProperty("id").GetString()!;
            var rank = route.GetProperty("rank").GetInt32();
            if (!coreRouteRanks.TryAdd(id, rank))
                throw new InvalidOperationException($"Routing policy declares core-task route '{id}' more than once.");
            var modelId = RouteModelId(knownModelIds, route, id);
            var thinkingLevel = route.GetProperty("thinkingLevel").GetString()!;
            coreRoutes[id] = (modelId, thinkingLevel);
            qualifying.Add(new QualifyingRoute(modelId, ThinkingRank(thinkingRanks, thinkingLevel), rank));
        }

        foreach (var id in RecommendableRoutes)
        {
            if (!coreRouteRanks.ContainsKey(id))
                throw new InvalidOperationException(
                    $"Routing policy declares no core-task route '{id}'; the recommendation ladder and the synchronized policy have drifted.");
        }

        if (routingRoot.TryGetProperty("providerFallbacks", out var fallbacks))
        {
            foreach (var fallback in fallbacks.EnumerateArray())
            {
                var id = fallback.GetProperty("id").GetString()!;
                var excluded = fallback.TryGetProperty("notForRouteIds", out var notFor)
                    ? Strings(notFor).ToHashSet(StringComparer.Ordinal)
                    : [];
                var forRouteIds = Strings(fallback.GetProperty("forRouteIds"));
                var fallbackModelId = RouteModelId(knownModelIds, fallback, id);
                var fallbackThinking = fallback.GetProperty("thinkingLevel").GetString()!;
                providerFallbacks.Add(new ProviderFallback(
                    id, fallbackModelId, fallbackThinking, forRouteIds, excluded.ToArray()));
                var rank = forRouteIds
                    .Where(routeId => !excluded.Contains(routeId))
                    .Select(routeId => (int?)coreRouteRanks.GetValueOrDefault(routeId, -1))
                    .Max();
                if (rank is null or < 0) continue;
                qualifying.Add(new QualifyingRoute(
                    fallbackModelId,
                    ThinkingRank(thinkingRanks, fallbackThinking),
                    rank.Value));
            }
        }

        return (coreRouteRanks, qualifying, coreRoutes, providerFallbacks);
    }

    /// <summary>
    /// Reads a route's <c>modelId</c> and asserts the policy also catalogs it. An id that matched
    /// no catalogued model would quietly rank that route unreachable, which reads as a stricter
    /// floor than the policy asserts.
    /// </summary>
    private static string RouteModelId(HashSet<string> knownModelIds, JsonElement route, string routeId)
    {
        var modelId = route.GetProperty("modelId").GetString()!;
        return knownModelIds.Contains(modelId)
            ? modelId
            : throw new InvalidOperationException(
                $"Routing policy route '{routeId}' names model '{modelId}', which the policy does not catalog.");
    }

    private static int ThinkingRank(Dictionary<string, int> thinkingRanks, string level) =>
        thinkingRanks.TryGetValue(level, out var rank)
            ? rank
            : throw new InvalidOperationException($"Routing policy references unknown thinking level '{level}'.");

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
            throw new ReviewModelSelectionException($"{label} contains unsupported characters.");
    }

    private void Add(string key, ReviewModelOption model)
    {
        if (!modelsByKey.TryAdd(key, model))
            throw new InvalidOperationException($"Duplicate model catalog key '{key}'.");
    }
}
