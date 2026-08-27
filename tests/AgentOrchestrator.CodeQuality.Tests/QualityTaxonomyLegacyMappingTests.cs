namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// One conformance vector per row of the dossier's compatibility mapping table
/// (docs/operations/data-model-taxonomy/index.html, section 8).
/// </summary>
public sealed class QualityTaxonomyLegacyMappingTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, ObservationAssessment.Pass, EvidenceStatus.Available, PolicyDecision.Allow)]
    [InlineData(SecurityVerdict.Warn, ObservationAssessment.Concern, EvidenceStatus.Available, null)]
    [InlineData(SecurityVerdict.Block, ObservationAssessment.Fail, EvidenceStatus.Available, PolicyDecision.Block)]
    [InlineData(SecurityVerdict.Unavailable, ObservationAssessment.Inconclusive, EvidenceStatus.Unavailable, null)]
    public void MapsSecurityVerdicts(
        SecurityVerdict verdict, ObservationAssessment expectedAssessment, EvidenceStatus expectedEvidenceStatus,
        PolicyDecision? expectedDecision)
    {
        var (assessment, evidenceStatus, decision) = QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict);

        Assert.Equal(expectedAssessment, assessment);
        Assert.Equal(expectedEvidenceStatus, evidenceStatus);
        Assert.Equal(expectedDecision, decision);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, ObservationAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, ObservationAssessment.Inconclusive)]
    public void MapsFlowVerdicts(FlowReviewVerdict verdict, ObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowVerdict(verdict));
    }

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, ObservationAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, ObservationAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, ObservationAssessment.NotAssessed)]
    public void MapsAttackCoverageVerdicts(AttackCoverageVerdict verdict, ObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackVerdict(verdict));
    }

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, ObservationChange.NoObservedDelta)]
    [InlineData(ChangeReviewVerdict.Improved, ObservationChange.Improved)]
    [InlineData(ChangeReviewVerdict.Neutral, ObservationChange.Unchanged)]
    [InlineData(ChangeReviewVerdict.Regression, ObservationChange.Regressed)]
    public void MapsChangeSummaryVerdicts(ChangeReviewVerdict verdict, ObservationChange expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeSummary(verdict));
    }

    [Theory]
    [InlineData("good", ObservationAssessment.Pass)]
    [InlineData("mixed", ObservationAssessment.Concern)]
    [InlineData("concerning", ObservationAssessment.Fail)]
    [InlineData("unknown", ObservationAssessment.Inconclusive)]
    public void MapsChangeAspectVerdicts(string verdict, ObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(verdict));
    }

    [Theory]
    [InlineData(FindingState.Open, ObservationLifecycleState.Open)]
    [InlineData(FindingState.Accepted, ObservationLifecycleState.AcceptedRisk)]
    [InlineData(FindingState.Waived, ObservationLifecycleState.Waived)]
    [InlineData(FindingState.FalsePositive, ObservationLifecycleState.FalsePositive)]
    [InlineData(FindingState.Resolved, ObservationLifecycleState.Resolved)]
    public void MapsFindingStates(FindingState state, ObservationLifecycleState expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(state));
    }

    [Theory]
    [InlineData("open", ObservationLifecycleState.Open)]
    [InlineData("accepted", ObservationLifecycleState.AcceptedRisk)]
    [InlineData("waived", ObservationLifecycleState.Waived)]
    [InlineData("false-positive", ObservationLifecycleState.FalsePositive)]
    [InlineData("falsePositive", ObservationLifecycleState.FalsePositive)]
    [InlineData("resolved", ObservationLifecycleState.Resolved)]
    public void MapsDriftedLifecycleSpellingsToTheSameCanonicalState(string legacySpelling, ObservationLifecycleState expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapLifecycleAlias(legacySpelling));
    }

    [Fact]
    public void ClassifiesValidJsonEvidenceAsAToolResult()
    {
        var classification = QualityTaxonomyLegacyMapping.ClassifyLegacyEvidence("{\"scanner\":\"gitleaks\"}");

        Assert.Equal(ObservationEvidenceKind.ToolResult, classification.Kind);
        Assert.True(classification.IsJson);
        Assert.Equal("{\"scanner\":\"gitleaks\"}", classification.PreservedContent);
    }

    [Fact]
    public void ClassifiesPlainTextEvidenceAsADocumentAndPreservesTheOriginalText()
    {
        var classification = QualityTaxonomyLegacyMapping.ClassifyLegacyEvidence("Call site evidence at line 42.");

        Assert.Equal(ObservationEvidenceKind.Document, classification.Kind);
        Assert.False(classification.IsJson);
        Assert.Equal("Call site evidence at line 42.", classification.PreservedContent);
    }

    [Fact]
    public void ClassifiesMalformedJsonAsPlainTextInsteadOfRejectingIt()
    {
        var classification = QualityTaxonomyLegacyMapping.ClassifyLegacyEvidence("{\"unterminated\": ");

        Assert.Equal(ObservationEvidenceKind.Document, classification.Kind);
        Assert.False(classification.IsJson);
    }
}
