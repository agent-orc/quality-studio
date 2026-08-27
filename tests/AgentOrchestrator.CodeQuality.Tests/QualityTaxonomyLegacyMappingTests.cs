namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, ObservationAssessment.Pass, ObservationEvidenceStatus.Available, ObservationDecision.Allow, null)]
    [InlineData(SecurityVerdict.Warn, ObservationAssessment.Concern, ObservationEvidenceStatus.Available, null, "security-sensor-agent-v1")]
    [InlineData(SecurityVerdict.Block, ObservationAssessment.Fail, ObservationEvidenceStatus.Available, ObservationDecision.Block, "security-sensor-agent-v1")]
    [InlineData(SecurityVerdict.Unavailable, ObservationAssessment.Inconclusive, ObservationEvidenceStatus.Unavailable, null, null)]
    public void SecurityVerdictMapsToAssessmentEvidenceStatusAndDecision(
        SecurityVerdict verdict, ObservationAssessment assessment, ObservationEvidenceStatus evidenceStatus,
        ObservationDecision? decision, string? policyRef)
    {
        var projection = QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict);

        Assert.Equal(assessment, projection.Assessment);
        Assert.Equal(evidenceStatus, projection.EvidenceStatus);
        Assert.Equal(decision, projection.Decision);
        Assert.Equal(policyRef, projection.PolicyRef);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, ObservationAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, ObservationAssessment.Inconclusive)]
    public void FlowVerdictMapsToAssessment(FlowReviewVerdict verdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowVerdict(verdict));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, ObservationAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, ObservationAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, ObservationAssessment.NotAssessed)]
    public void AttackVerdictMapsToAssessment(AttackCoverageVerdict verdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackVerdict(verdict));

    [Theory]
    [InlineData("no-quality-delta", ObservationChange.NoObservedDelta)]
    [InlineData("improved", ObservationChange.Improved)]
    [InlineData("neutral", ObservationChange.Unchanged)]
    [InlineData("regression", ObservationChange.Regressed)]
    public void ChangeSummaryMapsToTheChangeAxis(string legacySummary, ObservationChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeSummary(legacySummary));

    [Theory]
    [InlineData("good", ObservationAssessment.Pass)]
    [InlineData("mixed", ObservationAssessment.Concern)]
    [InlineData("concerning", ObservationAssessment.Fail)]
    [InlineData("unknown", ObservationAssessment.Inconclusive)]
    public void ChangeAspectVerdictMapsToAssessment(string legacyVerdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(legacyVerdict));

    [Theory]
    [InlineData("open", ObservationLifecycle.Open)]
    [InlineData("accepted", ObservationLifecycle.AcceptedRisk)]
    [InlineData("accepted-risk", ObservationLifecycle.AcceptedRisk)]
    [InlineData("waived", ObservationLifecycle.Waived)]
    [InlineData("false-positive", ObservationLifecycle.FalsePositive)]
    [InlineData("falsePositive", ObservationLifecycle.FalsePositive)]
    [InlineData("resolved", ObservationLifecycle.Resolved)]
    public void FindingStateMapsToTheLifecycleAxisIncludingDriftedSpellings(string legacyState, ObservationLifecycle expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(legacyState));

    [Fact]
    public void ValidJsonEvidenceStringBecomesToolResultEvidenceWithThePayloadPreserved()
    {
        const string evidence = "{\"scanner\":\"gitleaks\",\"rule\":\"generic-api-key\"}";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);

        Assert.Equal(ObservationEvidenceKind.ToolResult, mapped.Kind);
        Assert.Equal(evidence, mapped.RawPayload);
        Assert.StartsWith("sha256:", mapped.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextEvidenceStringBecomesDocumentEvidenceWithTheOriginalTextPreserved()
    {
        const string evidence = "Call site evidence: the token is never validated before use.";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);

        Assert.Equal(ObservationEvidenceKind.Document, mapped.Kind);
        Assert.Equal(evidence, mapped.RawPayload);
        Assert.Equal(evidence, mapped.Summary);
    }

    [Fact]
    public void EvidenceMappingIsDeterministicForTheSameInput()
    {
        const string evidence = "{\"a\":1}";

        var first = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);
        var second = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void UnknownLegacySpellingsAreRejectedRatherThanSilentlyCoerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMapping.MapChangeSummary("bogus"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMapping.MapChangeAspectVerdict("bogus"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMapping.MapFindingState("bogus"));
    }
}
