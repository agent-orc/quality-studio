namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ReviewModelCatalogTests
{
    private readonly ReviewModelCatalog catalog = ReviewModelCatalog.Default;

    [Fact]
    public void Snapshot_exposes_token_economy_provenance_and_capability_annotations()
    {
        Assert.Equal("agent-orc/token-economy", catalog.Snapshot.SourceRepository);
        Assert.Equal("28568bc197c109c60b9699901e655d783c09b82f", catalog.Snapshot.SourceCommit);
        Assert.Equal("2026-07-24", catalog.Snapshot.PolicyVersion);

        var sol = Assert.Single(catalog.Snapshot.Models, model => model.ModelId == "gpt-5.6-sol");
        Assert.Equal("codex", sol.CliType);
        Assert.Equal("frontier", sol.CapabilityTier);
        Assert.Equal("selectable", sol.RoutingStatus);
        Assert.Contains("correctness-critical", sol.Suitability, StringComparison.Ordinal);
        Assert.Contains("xhigh", sol.SupportedThinkingLevels);
        Assert.False(sol.PriceAvailable);
        Assert.True(sol.AvailableForNewRuns);
    }

    [Theory]
    [InlineData("claude-opus-4-1", "deprecated")]
    [InlineData("claude-mythos-5", "restricted")]
    [InlineData("gpt-5.5", "unsupported")]
    public void Non_routable_catalog_models_are_visible_as_evidence_but_rejected_for_new_runs(
        string modelId, string routingStatus)
    {
        var option = catalog.Find(modelId)!;
        Assert.Equal(routingStatus, option.RoutingStatus);
        Assert.False(option.AvailableForNewRuns);

        var exception = Assert.Throws<ArgumentException>(() =>
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
        Assert.Throws<ArgumentException>(() => catalog.Resolve("codex", "gpt-5.4-mini", "xhigh"));
    }

    [Fact]
    public void Compatible_free_text_model_is_a_deliberate_forward_compatibility_escape_hatch()
    {
        var custom = catalog.Resolve("codex", "gpt-6-review-preview", "high");

        Assert.Equal("gpt-6-review-preview", custom.Model);
        Assert.Equal("high", custom.ThinkingLevel);
        Assert.False(custom.Catalogued);
        Assert.Throws<ArgumentException>(() => catalog.Resolve("claude", "gpt-6-review-preview", "high"));
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
}
