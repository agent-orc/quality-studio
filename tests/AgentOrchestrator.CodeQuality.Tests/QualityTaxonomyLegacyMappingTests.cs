using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingTests
{
    [Theory]
    [InlineData("pass", Assessment.Pass, EvidenceStatus.Available, null, null)]
    [InlineData("warn", Assessment.Concern, null, null, QualityTaxonomyLegacyMapping.SecuritySensorAgentPolicyRef)]
    [InlineData("block", Assessment.Fail, null, Decision.Block, QualityTaxonomyLegacyMapping.SecuritySensorAgentPolicyRef)]
    [InlineData("unavailable", Assessment.Inconclusive, EvidenceStatus.Unavailable, null, null)]
    public void Security_verdicts_map_deterministically(
        string legacyVerdict, Assessment assessment, EvidenceStatus? evidenceStatus, Decision? decision, string? policyRef)
    {
        var mapping = QualityTaxonomyLegacyMapping.MapSecurityVerdict(legacyVerdict);

        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(decision, mapping.Decision);
        Assert.Equal(policyRef, mapping.PolicyRef);
    }

    [Theory]
    [InlineData("pass", Assessment.Pass)]
    [InlineData("fail", Assessment.Fail)]
    [InlineData("undetermined", Assessment.Inconclusive)]
    public void Flow_verdicts_map_deterministically(string legacyVerdict, Assessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowVerdict(legacyVerdict));

    [Theory]
    [InlineData("pass", Assessment.Pass)]
    [InlineData("finding", Assessment.Fail)]
    [InlineData("not-applicable", Assessment.NotApplicable)]
    [InlineData("not-yet-checked", Assessment.NotAssessed)]
    public void Attack_verdicts_map_deterministically(string legacyVerdict, Assessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackVerdict(legacyVerdict));

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void Change_summaries_map_deterministically(string legacySummary, QualityChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeSummary(legacySummary));

    [Theory]
    [InlineData("good", Assessment.Pass)]
    [InlineData("mixed", Assessment.Concern)]
    [InlineData("concerning", Assessment.Fail)]
    [InlineData("unknown", Assessment.Inconclusive)]
    public void Change_aspects_map_deterministically(string legacyAspect, Assessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspect(legacyAspect));

    [Theory]
    [InlineData("accepted", "accepted-risk", "accepted")]
    [InlineData("accepted-risk", "accepted-risk", null)]
    [InlineData("falsePositive", "false-positive", "falsePositive")]
    [InlineData("false-positive", "false-positive", null)]
    [InlineData("waived", "waived", null)]
    [InlineData("resolved", "resolved", null)]
    [InlineData("open", "open", null)]
    public void Finding_states_map_deterministically(string legacyState, string canonical, string? legacyAlias)
    {
        var mapping = QualityTaxonomyLegacyMapping.MapFindingState(legacyState);

        Assert.Equal(canonical, mapping.Canonical);
        Assert.Equal(legacyAlias, mapping.LegacyAlias);
    }

    [Fact]
    public void Valid_json_evidence_string_becomes_typed_tool_result_evidence_preserving_the_raw_payload()
    {
        const string legacy = """{"tool":"eslint","findings":2}""";

        var evidence = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", legacy);

        Assert.Equal(EvidenceKind.ToolResult, evidence.Kind);
        Assert.Equal("application/json", evidence.MediaType);
        Assert.Equal(legacy, evidence.LegacyRaw);
        Assert.StartsWith("sha256:", evidence.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_evidence_string_becomes_typed_evidence_preserving_the_original_text()
    {
        const string legacy = "The dependency scanner reported a high-severity CVE.";

        var evidence = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-2", legacy);

        Assert.Equal(EvidenceKind.Document, evidence.Kind);
        Assert.Null(evidence.MediaType);
        Assert.Equal(legacy, evidence.LegacyRaw);
        Assert.Equal(legacy, evidence.Summary);
    }

    [Theory]
    [InlineData("security", "totally-unknown")]
    [InlineData("flow", "totally-unknown")]
    [InlineData("attack", "totally-unknown")]
    [InlineData("change-summary", "totally-unknown")]
    [InlineData("change-aspect", "totally-unknown")]
    [InlineData("finding-state", "totally-unknown")]
    public void Unknown_legacy_spellings_are_rejected_rather_than_silently_defaulted(string axis, string value)
    {
        Action act = axis switch
        {
            "security" => () => QualityTaxonomyLegacyMapping.MapSecurityVerdict(value),
            "flow" => () => QualityTaxonomyLegacyMapping.MapFlowVerdict(value),
            "attack" => () => QualityTaxonomyLegacyMapping.MapAttackVerdict(value),
            "change-summary" => () => QualityTaxonomyLegacyMapping.MapChangeSummary(value),
            "change-aspect" => () => QualityTaxonomyLegacyMapping.MapChangeAspect(value),
            "finding-state" => () => QualityTaxonomyLegacyMapping.MapFindingState(value),
            _ => throw new InvalidOperationException(),
        };

        Assert.Throws<ArgumentOutOfRangeException>(act);
    }
}
