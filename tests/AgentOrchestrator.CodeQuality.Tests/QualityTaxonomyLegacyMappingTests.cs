namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// One test per row of the dossier's compatibility mapping table
/// (docs/operations/data-model-taxonomy/index.html, section 8).
/// </summary>
public sealed class QualityTaxonomyLegacyMappingTests
{
    [Fact]
    public void Security_pass_maps_to_pass_available_allow()
    {
        var mapped = QualityTaxonomyLegacyMapping.MapSecurityVerdict(SecurityVerdict.Pass);

        Assert.Equal(QualityAssessment.Pass, mapped.Assessment);
        Assert.Equal(QualityEvidenceStatus.Available, mapped.EvidenceStatus);
        Assert.Equal("allow", mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Fact]
    public void Security_warn_maps_to_concern_with_retained_policy_ref()
    {
        var mapped = QualityTaxonomyLegacyMapping.MapSecurityVerdict(SecurityVerdict.Warn);

        Assert.Equal(QualityAssessment.Concern, mapped.Assessment);
        Assert.Null(mapped.Decision);
        Assert.Equal("security-sensor-agent-v1", mapped.PolicyRef);
    }

    [Fact]
    public void Security_block_maps_to_fail_and_block_decision()
    {
        var mapped = QualityTaxonomyLegacyMapping.MapSecurityVerdict(SecurityVerdict.Block);

        Assert.Equal(QualityAssessment.Fail, mapped.Assessment);
        Assert.Equal("block", mapped.Decision);
    }

    [Fact]
    public void Security_unavailable_maps_to_inconclusive_and_unavailable_evidence()
    {
        var mapped = QualityTaxonomyLegacyMapping.MapSecurityVerdict(SecurityVerdict.Unavailable);

        Assert.Equal(QualityAssessment.Inconclusive, mapped.Assessment);
        Assert.Equal(QualityEvidenceStatus.Unavailable, mapped.EvidenceStatus);
        Assert.Null(mapped.Decision);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, QualityAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, QualityAssessment.Inconclusive)]
    public void Flow_verdicts_map_onto_the_assessment_axis(FlowReviewVerdict legacy, QualityAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowReviewVerdict(legacy));
    }

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, QualityAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, QualityAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, QualityAssessment.NotAssessed)]
    public void Attack_coverage_verdicts_map_onto_the_assessment_axis(AttackCoverageVerdict legacy, QualityAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackCoverageVerdict(legacy));
    }

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, "no-observed-delta")]
    [InlineData(ChangeReviewVerdict.Improved, "improved")]
    [InlineData(ChangeReviewVerdict.Neutral, "unchanged")]
    [InlineData(ChangeReviewVerdict.Regression, "regressed")]
    public void Change_summary_verdicts_map_onto_the_change_axis(ChangeReviewVerdict legacy, string expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeReviewVerdict(legacy));
        Assert.True(QualityTaxonomyCatalogue.IsKnownAxisTerm("change", expected));
    }

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void Change_aspect_verdicts_map_onto_the_assessment_axis(string legacy, QualityAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(legacy));
    }

    [Theory]
    [InlineData(FindingState.Open, "open")]
    [InlineData(FindingState.Accepted, "accepted-risk")]
    [InlineData(FindingState.Waived, "waived")]
    [InlineData(FindingState.FalsePositive, "false-positive")]
    [InlineData(FindingState.Resolved, "resolved")]
    public void Finding_states_map_onto_the_lifecycle_axis(FindingState legacy, string expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(legacy));
        Assert.True(QualityTaxonomyCatalogue.IsKnownAxisTerm("lifecycle", expected));
    }

    [Theory]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    public void Drifted_lifecycle_spellings_normalize_to_the_canonical_id(string legacy, string expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.NormalizeLifecycleAlias(legacy));
    }

    [Fact]
    public void Valid_json_evidence_string_classifies_as_tool_result()
    {
        Assert.Equal(QualityEvidenceKind.ToolResult,
            QualityTaxonomyLegacyMapping.ClassifyLegacyEvidenceString("""{"rule":"gitleaks","match":"redacted"}"""));
    }

    [Fact]
    public void Plain_text_evidence_string_classifies_as_document()
    {
        Assert.Equal(QualityEvidenceKind.Document,
            QualityTaxonomyLegacyMapping.ClassifyLegacyEvidenceString("The retry loop can spin without a bound."));
    }
}
