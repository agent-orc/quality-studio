namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// One conformance vector per row of the dossier's section 8 "Compatibility mappings" table
/// (docs/operations/data-model-taxonomy/index.html), proving every current verdict/state spelling
/// maps deterministically onto the quality-studio/core@1.0.0 axes.
/// </summary>
public sealed class LegacyTaxonomyMapperTests
{
    [Fact]
    public void Security_verdict_pass_maps_to_pass_available_allow()
    {
        var mapped = LegacyTaxonomyMapper.MapSecurityVerdict("pass");

        Assert.Equal("pass", mapped.Assessment);
        Assert.Equal("available", mapped.EvidenceStatus);
        Assert.Equal("allow", mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Fact]
    public void Security_verdict_warn_maps_to_concern_with_no_decision()
    {
        var mapped = LegacyTaxonomyMapper.MapSecurityVerdict("warn");

        Assert.Equal("concern", mapped.Assessment);
        Assert.Equal("available", mapped.EvidenceStatus);
        Assert.Null(mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Fact]
    public void Security_verdict_block_maps_to_fail_block()
    {
        var mapped = LegacyTaxonomyMapper.MapSecurityVerdict("block");

        Assert.Equal("fail", mapped.Assessment);
        Assert.Equal("block", mapped.Decision);
    }

    [Fact]
    public void Security_verdict_unavailable_maps_to_inconclusive_unavailable()
    {
        var mapped = LegacyTaxonomyMapper.MapSecurityVerdict("unavailable");

        Assert.Equal("inconclusive", mapped.Assessment);
        Assert.Equal("unavailable", mapped.EvidenceStatus);
        Assert.Null(mapped.Decision);
    }

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("fail", "fail")]
    [InlineData("undetermined", "inconclusive")]
    public void Flow_verdict_maps_onto_the_assessment_axis(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapper.MapFlowVerdict(legacy));

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("finding", "fail")]
    [InlineData("not-applicable", "not-applicable")]
    [InlineData("not-yet-checked", "not-assessed")]
    public void Attack_verdict_maps_onto_the_assessment_axis(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapper.MapAttackVerdict(legacy));

    [Theory]
    [InlineData("no-quality-delta", "no-observed-delta")]
    [InlineData("improved", "improved")]
    [InlineData("neutral", "unchanged")]
    [InlineData("regression", "regressed")]
    public void Change_summary_maps_onto_the_change_axis(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapper.MapChangeSummary(legacy));

    [Theory]
    [InlineData("good", "pass")]
    [InlineData("mixed", "concern")]
    [InlineData("concerning", "fail")]
    [InlineData("unknown", "inconclusive")]
    public void Change_aspect_verdict_maps_onto_the_assessment_axis(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapper.MapChangeAspectVerdict(legacy));

    [Theory]
    [InlineData("open", "open")]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("waived", "waived")]
    [InlineData("false-positive", "false-positive")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("resolved", "resolved")]
    public void Finding_state_maps_onto_the_lifecycle_axis(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapper.MapFindingState(legacy));

    [Fact]
    public void Evidence_string_with_valid_json_maps_to_typed_tool_result_evidence()
    {
        const string legacy = """{"rule":"CA2100","severity":"high"}""";

        var mapped = LegacyTaxonomyMapper.MapEvidenceString(legacy);

        Assert.Equal("tool-result", mapped.Kind);
        Assert.Equal("application/json", mapped.MediaType);
        Assert.Equal(legacy, mapped.PreservedText);
        Assert.StartsWith("sha256:", mapped.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_string_with_plain_text_maps_to_typed_evidence_with_preserved_text()
    {
        const string legacy = "Call site evidence: the return value is never checked.";

        var mapped = LegacyTaxonomyMapper.MapEvidenceString(legacy);

        Assert.Equal("document", mapped.Kind);
        Assert.Null(mapped.MediaType);
        Assert.Equal(legacy, mapped.PreservedText);
        Assert.Equal(legacy, mapped.Summary);
    }

    [Fact]
    public void Evidence_string_mapping_never_discards_malformed_json_looking_text()
    {
        const string legacy = "{ not actually json";

        var mapped = LegacyTaxonomyMapper.MapEvidenceString(legacy);

        Assert.Equal("document", mapped.Kind);
        Assert.Equal(legacy, mapped.PreservedText);
    }

    [Theory]
    [InlineData("degraded")]
    [InlineData("")]
    public void Unknown_legacy_values_are_rejected_rather_than_silently_coerced(string legacyVerdict) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyTaxonomyMapper.MapSecurityVerdict(legacyVerdict));
}
