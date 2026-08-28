namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// One test per row of the dossier's section 8 "Compatibility mappings" table
/// (docs/operations/data-model-taxonomy/index.html#mapping). Every current verdict/state
/// spelling must map deterministically onto the core v1 taxonomy.
/// </summary>
public sealed class QualityTaxonomyLegacyMappingsTests
{
    [Fact]
    public void SecurityPassMapsToPassWithAvailableEvidenceAndOptionalAllow()
    {
        Assert.Equal(QualityAssessment.Pass, QualityTaxonomyLegacyMappings.FromSecurityVerdict(SecurityVerdict.Pass));
        Assert.Equal(QualityEvidenceStatus.Available,
            QualityTaxonomyLegacyMappings.EvidenceStatusFromSecurityVerdict(SecurityVerdict.Pass));
        Assert.Null(QualityTaxonomyLegacyMappings.DecisionFromSecurityVerdict(SecurityVerdict.Pass));
    }

    [Fact]
    public void SecurityWarnMapsToConcernAndRetainsThePolicyRef()
    {
        Assert.Equal(QualityAssessment.Concern, QualityTaxonomyLegacyMappings.FromSecurityVerdict(SecurityVerdict.Warn));
        Assert.Equal(QualityTaxonomyLegacyMappings.SecuritySensorPolicyRef,
            QualityTaxonomyLegacyMappings.PolicyRefFromSecurityVerdict(SecurityVerdict.Warn));
    }

    [Fact]
    public void SecurityBlockMapsToFailWithABlockDecision()
    {
        Assert.Equal(QualityAssessment.Fail, QualityTaxonomyLegacyMappings.FromSecurityVerdict(SecurityVerdict.Block));
        Assert.Equal(QualityDecision.Block, QualityTaxonomyLegacyMappings.DecisionFromSecurityVerdict(SecurityVerdict.Block));
    }

    [Fact]
    public void SecurityUnavailableMapsToInconclusiveWithUnavailableEvidence()
    {
        Assert.Equal(QualityAssessment.Inconclusive,
            QualityTaxonomyLegacyMappings.FromSecurityVerdict(SecurityVerdict.Unavailable));
        Assert.Equal(QualityEvidenceStatus.Unavailable,
            QualityTaxonomyLegacyMappings.EvidenceStatusFromSecurityVerdict(SecurityVerdict.Unavailable));
        Assert.Equal(QualityTaxonomyLegacyMappings.SecuritySensorPolicyRef,
            QualityTaxonomyLegacyMappings.PolicyRefFromSecurityVerdict(SecurityVerdict.Unavailable));
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, QualityAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, QualityAssessment.Inconclusive)]
    public void FlowVerdictMapsOntoTheAssessmentAxis(FlowReviewVerdict legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.FromFlowVerdict(legacy));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, QualityAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, QualityAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, QualityAssessment.NotAssessed)]
    public void AttackVerdictMapsOntoTheAssessmentAxis(AttackCoverageVerdict legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.FromAttackVerdict(legacy));

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, QualityChange.NoObservedDelta)]
    [InlineData(ChangeReviewVerdict.Improved, QualityChange.Improved)]
    [InlineData(ChangeReviewVerdict.Neutral, QualityChange.Unchanged)]
    [InlineData(ChangeReviewVerdict.Regression, QualityChange.Regressed)]
    public void ChangeSummaryMapsOntoTheChangeAxis(ChangeReviewVerdict legacy, QualityChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.FromChangeSummary(legacy));

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void ChangeAspectVerdictMapsOntoTheAssessmentAxis(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.FromChangeAspectVerdict(legacy));

    [Fact]
    public void FindingStateAcceptedMapsToAcceptedRisk() =>
        Assert.Equal(QualityLifecycleState.AcceptedRisk,
            QualityTaxonomyLegacyMappings.FromFindingState(FindingState.Accepted));

    [Theory]
    [InlineData("false-positive")]
    [InlineData("falsePositive")]
    public void BothFalsePositiveSpellingsMapToTheSameLifecycleState(string legacySpelling) =>
        Assert.Equal(QualityLifecycleState.FalsePositive,
            QualityTaxonomyLegacyMappings.FromFindingStateSpelling(legacySpelling));

    [Fact]
    public void ValidJsonEvidenceStringBecomesATypedToolResultWithADigest()
    {
        var evidence = QualityTaxonomyLegacyMappings.FromEvidenceString("ev-1", "{\"rule\":\"S1234\",\"pass\":false}");

        Assert.Equal(QualityEvidenceKind.ToolResult, evidence.Kind);
        Assert.NotNull(evidence.ContentHash);
        Assert.StartsWith("sha256:", evidence.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextEvidenceStringIsPreservedAsATypedDocumentWithSummary()
    {
        var evidence = QualityTaxonomyLegacyMappings.FromEvidenceString("ev-1", "Unchecked value reaches the dereference.");

        Assert.Equal(QualityEvidenceKind.Document, evidence.Kind);
        Assert.Equal("Unchecked value reaches the dereference.", evidence.Summary);
        Assert.NotNull(evidence.ContentHash);
    }

    [Fact]
    public void LongPlainTextEvidenceIsTruncatedInTheSummaryButTheDigestCoversTheFullText()
    {
        var longText = new string('a', 1000);

        var evidence = QualityTaxonomyLegacyMappings.FromEvidenceString("ev-1", longText);

        Assert.Equal(240, evidence.Summary.Length);
        Assert.NotNull(evidence.ContentHash);
    }
}
