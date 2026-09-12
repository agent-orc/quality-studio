namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewModelCatalogTests
{
    private readonly ReviewModelCatalog catalog = ReviewModelCatalog.Default;

    [Fact]
    public void Snapshot_exposes_token_economy_provenance_and_capability_annotations()
    {
        Assert.Equal("agent-orc/token-economy", catalog.Snapshot.SourceRepository);
        Assert.Equal("98ddcc91fba414919231e242de01dc022aed74dd", catalog.Snapshot.SourceCommit);
        Assert.Equal("2026-09-12", catalog.Snapshot.PolicyVersion);
        Assert.Equal(22, catalog.Snapshot.Models.Count);
        Assert.All(catalog.Snapshot.Models, model => Assert.True(model.PriceAvailable));

        var sol = Assert.Single(catalog.Snapshot.Models, model => model.ModelId == "gpt-5.6-sol");
        Assert.Equal("codex", sol.CliType);
        Assert.Equal("frontier", sol.CapabilityTier);
        Assert.Equal("selectable", sol.RoutingStatus);
        Assert.Contains("correctness-critical", sol.Suitability, StringComparison.Ordinal);
        Assert.Contains("xhigh", sol.SupportedThinkingLevels);
        Assert.True(sol.PriceAvailable);
        Assert.True(sol.AvailableForNewRuns);
    }

    [Theory]
    [InlineData("gpt-6-astra", "astra", "codex", "codex")]
    [InlineData("claude-fable-5-1", "claude-fable-5-1", "claude-code", "claude")]
    public void Newly_supported_models_are_explicit_provisional_choices_without_default_floor_equivalence(
        string modelId, string requestedId, string requestedCli, string canonicalCli)
    {
        var option = catalog.Find(modelId)!;
        Assert.Equal("selectable", option.RoutingStatus);
        Assert.Equal("frontier", option.CapabilityTier);
        Assert.True(option.Provisional);
        Assert.Equal("provisional", option.EvidenceStatus);
        Assert.True(option.AvailableForNewRuns);
        Assert.True(option.PriceAvailable);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, option.SupportedThinkingLevels);

        var selected = catalog.Resolve(requestedCli, requestedId, "MAX");
        Assert.Equal(modelId, selected.Model);
        Assert.Equal(canonicalCli, selected.CliType);
        Assert.Equal("max", selected.ThinkingLevel);
        Assert.True(selected.Catalogued);
        Assert.Throws<ReviewModelSelectionException>(() => catalog.Resolve(requestedCli, requestedId, "ultra"));
        Assert.Throws<ReviewModelSelectionException>(() =>
            catalog.Resolve(canonicalCli == "codex" ? "claude" : "codex", modelId, "max"));

        // Selectable support does not declare a qualified core route or provider fallback.
        var small = catalog.Recommend("code", ReviewLevel.File, 1);
        var security = catalog.Recommend("security", ReviewLevel.File, 1);
        Assert.True(catalog.IsBelowCorrectnessFloor(selected, small));
        Assert.True(catalog.IsBelowCorrectnessFloor(selected, security));
        Assert.NotEqual(modelId, catalog.ResolveDefault(canonicalCli, small)!.Model);
        Assert.NotEqual(modelId, catalog.ResolveDefault(canonicalCli, security)!.Model);
    }

    [Theory]
    [InlineData("claude-opus-4-1", "deprecated")]
    [InlineData("gpt-5.5-cyber-preview", "restricted")]
    [InlineData("gpt-5.5", "unsupported")]
    [InlineData("claude-opus-5", "unsupported")]
    public void Non_routable_catalog_models_are_visible_as_evidence_but_rejected_for_new_runs(
        string modelId, string routingStatus)
    {
        var option = catalog.Find(modelId)!;
        Assert.Equal(routingStatus, option.RoutingStatus);
        Assert.False(option.AvailableForNewRuns);

        var exception = Assert.Throws<ReviewModelSelectionException>(() =>
            catalog.Resolve(option.CliType, modelId, option.SupportedThinkingLevels[0]));
        Assert.Contains(routingStatus, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Selection_canonicalizes_alias_and_validates_thinking_level()
    {
        var selection = catalog.Resolve("codex", "sol", "XHIGH");

        Assert.Equal("gpt-5.6-sol", selection.Model);
        Assert.Equal("xhigh", selection.ThinkingLevel);
        Assert.Equal("codex", selection.CliType);
        Assert.True(selection.Catalogued);
        Assert.Throws<ReviewModelSelectionException>(() => catalog.Resolve("codex", "gpt-5.4-mini", "xhigh"));
    }

    [Fact]
    public void Compatible_free_text_model_is_a_deliberate_forward_compatibility_escape_hatch()
    {
        var custom = catalog.Resolve("codex", "gpt-6-review-preview", "high");

        Assert.Equal("gpt-6-review-preview", custom.Model);
        Assert.Equal("high", custom.ThinkingLevel);
        Assert.False(custom.Catalogued);
        Assert.Throws<ReviewModelSelectionException>(() => catalog.Resolve("claude", "gpt-6-review-preview", "high"));
    }

    [Fact]
    public void Omitting_model_and_thinking_keeps_runner_defaults_unmodified()
    {
        var selection = catalog.Resolve(null, null, null);

        Assert.Equal("codex", selection.CliType);
        Assert.Null(selection.Model);
        Assert.Null(selection.ThinkingLevel);
    }

    [Fact]
    public void Recommendation_applies_scope_score_and_security_floor_without_quota_demotion()
    {
        var file = catalog.Recommend("code", ReviewLevel.File, 1);
        var aggregate = catalog.Recommend("code", ReviewLevel.Project, 80);
        var security = catalog.Recommend("security", ReviewLevel.File, 1);

        Assert.Equal("gpt-5.6-terra", file.RecommendedModel);
        Assert.Equal("luna-medium", file.CorrectnessFloor);
        Assert.Equal("gpt-5.6-sol", aggregate.RecommendedModel);
        Assert.Equal("medium", aggregate.RecommendedThinkingLevel);
        Assert.Equal("sol-medium", aggregate.CorrectnessFloor);
        Assert.Equal("xhigh", security.RecommendedThinkingLevel);
        Assert.Equal("sol-xhigh", security.CorrectnessFloor);
        Assert.Contains("Price and quota do not lower", security.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_route_below_hard_floor_is_detected_but_runner_default_remains_unknown()
    {
        var recommendation = catalog.Recommend("security", ReviewLevel.File, 1);

        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-luna", "medium"), recommendation));
        Assert.False(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-sol", "xhigh"), recommendation));
        Assert.False(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-sol", "ultra"), recommendation));
        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.4-mini", "high"), recommendation));
        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-6-review-preview", "high"), recommendation));
        Assert.False(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", null, null), recommendation));

        var broad = catalog.Recommend("code", ReviewLevel.Project, 20);
        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-sol", "low"), broad));
        Assert.False(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("claude", "claude-sonnet-5", "high"), broad));

        var small = catalog.Recommend("code", ReviewLevel.File, 1);
        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-luna", "low"), small));
    }

    [Fact]
    public void Provider_fallback_qualifies_only_at_the_routes_it_is_declared_for()
    {
        var security = catalog.Recommend("security", ReviewLevel.File, 1);
        var broad = catalog.Recommend("code", ReviewLevel.Project, 20);
        var sonnet = catalog.Resolve("claude", "claude-sonnet-5", "max");

        // claude-sonnet-5-high is declared for terra-medium and sol-medium only, so the strongest
        // thinking level the model supports still does not reach the sol-xhigh security floor.
        Assert.Equal("sol-medium", broad.CorrectnessFloor);
        Assert.False(catalog.IsBelowCorrectnessFloor(sonnet, broad));
        Assert.Equal("sol-xhigh", security.CorrectnessFloor);
        Assert.True(catalog.IsBelowCorrectnessFloor(sonnet, security));
    }

    [Fact]
    public void Unrecognized_correctness_floor_reports_below_floor_rather_than_skipping_the_gate()
    {
        var recommendation = catalog.Recommend("security", ReviewLevel.File, 1) with
        {
            CorrectnessFloor = "sol-xhigh-renamed-upstream",
        };

        Assert.True(catalog.IsBelowCorrectnessFloor(
            catalog.Resolve("codex", "gpt-5.6-sol", "xhigh"), recommendation));
    }

    // Pins the upstream routing gap that keeps the operator's Claude-first policy unrunnable:
    // Token Economy still ships claude-opus-5 as unsupported with no workflow role, so the policy
    // qualifies it for no core-task route and it can neither start a run nor clear any floor above
    // the lightest. Promoting it upstream fails this test, which is the signal to re-sync and
    // record a run on the promoted route.
    [Fact]
    public void Claude_opus_5_is_priced_but_not_yet_qualified_by_the_routing_policy()
    {
        var opus = catalog.Find("claude-opus-5")!;

        Assert.Equal("unsupported", opus.RoutingStatus);
        Assert.False(opus.AvailableForNewRuns);
        Assert.True(opus.PriceAvailable);

        var broad = catalog.Recommend("code", ReviewLevel.Project, 20);
        Assert.True(catalog.IsBelowCorrectnessFloor(
            new ReviewModelSelection("claude", "claude-opus-5", "max", true), broad));
    }

    // The CLI reaches the incompatible-model message, and that message is the one the API echoes as
    // problem detail, so an unvetted CLI would be reflected to the caller verbatim.
    [Fact]
    public void Cli_type_is_held_to_the_identifier_rule_before_it_can_reach_an_echoed_message()
    {
        var exception = Assert.Throws<ReviewModelSelectionException>(() =>
            catalog.Resolve("codex<script>", "gpt-5.6-sol", "medium"));
        Assert.DoesNotContain("<script>", exception.Message, StringComparison.Ordinal);

        // An unknown but well-formed CLI stays usable: it is the forward-compatibility path.
        Assert.Equal("test-agent", catalog.Resolve("test-agent", "gpt-5.6-sol", "medium").CliType);
    }

    [Fact]
    public void Default_route_for_codex_is_the_policy_recommendation()
    {
        var recommendation = catalog.Recommend("code", ReviewLevel.File, 1);
        var route = catalog.ResolveDefault("codex", recommendation);

        Assert.NotNull(route);
        Assert.Equal("codex", route.CliType);
        Assert.Equal(recommendation.RecommendedModel, route.Model);
        Assert.Equal(recommendation.RecommendedThinkingLevel, route.ThinkingLevel);
        Assert.True(route.Catalogued);
    }

    [Fact]
    public void Default_route_for_claude_is_the_provider_fallback_declared_for_the_recommended_route()
    {
        // Ten files of code review score into the terra-medium band, which the policy backs with
        // its claude-sonnet-5/high fallback.
        var recommendation = catalog.Recommend("code", ReviewLevel.File, 10);
        Assert.Equal("gpt-5.6-terra", recommendation.RecommendedModel);

        var route = catalog.ResolveDefault("claude-code", recommendation);

        Assert.NotNull(route);
        Assert.Equal("claude", route.CliType);
        Assert.Equal("claude-sonnet-5", route.Model);
        Assert.Equal("high", route.ThinkingLevel);
        Assert.True(route.Catalogued);
    }

    [Fact]
    public void Default_route_for_claude_still_names_a_model_when_the_policy_withholds_every_fallback()
    {
        // Security reviews sit on the sol-xhigh floor, from which the policy excludes its claude
        // fallback. The run still needs a real model id, and the recommendation keeps naming the
        // floor the chosen model does not reach.
        var recommendation = catalog.Recommend("security", ReviewLevel.File, 1);
        Assert.Equal("sol-xhigh", recommendation.CorrectnessFloor);

        var route = catalog.ResolveDefault("claude", recommendation);

        Assert.NotNull(route);
        Assert.Equal("claude-sonnet-5", route.Model);
        Assert.NotNull(route.ThinkingLevel);
        Assert.True(catalog.IsBelowCorrectnessFloor(route, recommendation));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("antigravity")]
    [InlineData("test-agent")]
    public void Default_route_is_absent_for_a_cli_the_policy_does_not_route(string cliType)
    {
        Assert.Null(catalog.ResolveDefault(cliType, catalog.Recommend("code", ReviewLevel.File, 1)));
    }
}
