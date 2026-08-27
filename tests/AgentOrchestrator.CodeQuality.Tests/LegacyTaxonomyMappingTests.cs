namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class LegacyTaxonomyMappingTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, TaxonomyAssessment.Pass, TaxonomyEvidenceStatus.Available, TaxonomyDecision.Allow, null)]
    [InlineData(SecurityVerdict.Warn, TaxonomyAssessment.Concern, TaxonomyEvidenceStatus.Available, null, LegacyTaxonomyMapping.SecuritySensorPolicyId)]
    [InlineData(SecurityVerdict.Block, TaxonomyAssessment.Fail, TaxonomyEvidenceStatus.Available, TaxonomyDecision.Block, LegacyTaxonomyMapping.SecuritySensorPolicyId)]
    [InlineData(SecurityVerdict.Unavailable, TaxonomyAssessment.Inconclusive, TaxonomyEvidenceStatus.Unavailable, null, null)]
    public void SecurityVerdictMapsOntoAssessmentAndPolicyDisposition(
        SecurityVerdict verdict, TaxonomyAssessment assessment, TaxonomyEvidenceStatus evidenceStatus,
        TaxonomyDecision? decision, string? policyRef)
    {
        var mapping = LegacyTaxonomyMapping.MapSecurityVerdict(verdict);

        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(decision, mapping.Decision);
        Assert.Equal(policyRef, mapping.PolicyRef);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, TaxonomyAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, TaxonomyAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, TaxonomyAssessment.Inconclusive)]
    public void FlowVerdictMapsOntoTheAssessmentAxis(FlowReviewVerdict verdict, TaxonomyAssessment expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapFlowVerdict(verdict));
    }

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, TaxonomyAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, TaxonomyAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, TaxonomyAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, TaxonomyAssessment.NotAssessed)]
    public void AttackVerdictMapsOntoTheAssessmentAxis(AttackCoverageVerdict verdict, TaxonomyAssessment expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapAttackVerdict(verdict));
    }

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, TaxonomyChange.NoObservedDelta)]
    [InlineData(ChangeReviewVerdict.Improved, TaxonomyChange.Improved)]
    [InlineData(ChangeReviewVerdict.Neutral, TaxonomyChange.Unchanged)]
    [InlineData(ChangeReviewVerdict.Regression, TaxonomyChange.Regressed)]
    public void ChangeSummaryMapsOntoTheChangeAxis(ChangeReviewVerdict verdict, TaxonomyChange expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapChangeSummary(verdict));
    }

    [Theory]
    [InlineData("good", TaxonomyAssessment.Pass)]
    [InlineData("mixed", TaxonomyAssessment.Concern)]
    [InlineData("concerning", TaxonomyAssessment.Fail)]
    [InlineData("unknown", TaxonomyAssessment.Inconclusive)]
    public void ChangeAspectVerdictMapsOntoTheAssessmentAxis(string verdict, TaxonomyAssessment expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapChangeAspectVerdict(verdict));
    }

    [Fact]
    public void ChangeAspectVerdictRejectsAnUnmappedSpelling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyTaxonomyMapping.MapChangeAspectVerdict("not-reviewed"));
    }

    [Theory]
    [InlineData(FindingState.Open, TaxonomyLifecycle.Open)]
    [InlineData(FindingState.Accepted, TaxonomyLifecycle.AcceptedRisk)]
    [InlineData(FindingState.Waived, TaxonomyLifecycle.Waived)]
    [InlineData(FindingState.FalsePositive, TaxonomyLifecycle.FalsePositive)]
    [InlineData(FindingState.Resolved, TaxonomyLifecycle.Resolved)]
    public void FindingStateMapsOntoTheLifecycleAxis(FindingState state, TaxonomyLifecycle expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapFindingState(state));
    }

    [Theory]
    [InlineData("accepted", TaxonomyLifecycle.AcceptedRisk)]
    [InlineData("accepted-risk", TaxonomyLifecycle.AcceptedRisk)]
    [InlineData("falsePositive", TaxonomyLifecycle.FalsePositive)]
    [InlineData("false-positive", TaxonomyLifecycle.FalsePositive)]
    [InlineData("open", TaxonomyLifecycle.Open)]
    [InlineData("waived", TaxonomyLifecycle.Waived)]
    [InlineData("resolved", TaxonomyLifecycle.Resolved)]
    public void FindingStateSpellingDriftResolvesToTheSameLifecycleTerm(string spelling, TaxonomyLifecycle expected)
    {
        Assert.Equal(expected, LegacyTaxonomyMapping.MapFindingStateSpelling(spelling));
    }

    [Fact]
    public void BothFalsePositiveSpellingsAgree()
    {
        Assert.Equal(
            LegacyTaxonomyMapping.MapFindingStateSpelling("falsePositive"),
            LegacyTaxonomyMapping.MapFindingStateSpelling("false-positive"));
    }

    [Fact]
    public void BothAcceptedSpellingsAgree()
    {
        Assert.Equal(
            LegacyTaxonomyMapping.MapFindingStateSpelling("accepted"),
            LegacyTaxonomyMapping.MapFindingStateSpelling("accepted-risk"));
    }
}
