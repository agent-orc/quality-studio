namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecision.Allow)]
    [InlineData(SecurityVerdict.Warn, QualityAssessment.Concern, QualityEvidenceStatus.Available, null)]
    [InlineData(SecurityVerdict.Block, QualityAssessment.Fail, QualityEvidenceStatus.Available, QualityDecision.Block)]
    [InlineData(SecurityVerdict.Unavailable, QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null)]
    public void SecurityVerdictMapsDeterministically(
        SecurityVerdict verdict, QualityAssessment assessment, QualityEvidenceStatus evidenceStatus, QualityDecision? decision)
    {
        var mapping = QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict);

        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(decision, mapping.Decision);
        Assert.Equal(QualityTaxonomyLegacyMapping.SecuritySensorPolicyRef, mapping.PolicyRef);
        Assert.Equal(mapping, QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict));
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, QualityAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, QualityAssessment.Inconclusive)]
    public void FlowReviewVerdictMapsDeterministically(FlowReviewVerdict verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowReviewVerdict(verdict));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, QualityAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, QualityAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, QualityAssessment.NotAssessed)]
    public void AttackCoverageVerdictMapsDeterministically(AttackCoverageVerdict verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackCoverageVerdict(verdict));

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, QualityChange.NoObservedDelta)]
    [InlineData(ChangeReviewVerdict.Improved, QualityChange.Improved)]
    [InlineData(ChangeReviewVerdict.Neutral, QualityChange.Unchanged)]
    [InlineData(ChangeReviewVerdict.Regression, QualityChange.Regressed)]
    public void ChangeReviewVerdictMapsDeterministically(ChangeReviewVerdict verdict, QualityChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeReviewVerdict(verdict));

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    [InlineData("not-reviewed", QualityAssessment.NotAssessed)]
    public void ChangeAspectVerdictMapsDeterministically(string verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(verdict));

    [Fact]
    public void ChangeAspectVerdictRejectsAnUnknownSpellingInsteadOfGuessing() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMapping.MapChangeAspectVerdict("mostly-fine"));

    [Theory]
    [InlineData(FindingState.Open, QualityLifecycle.Open)]
    [InlineData(FindingState.Accepted, QualityLifecycle.AcceptedRisk)]
    [InlineData(FindingState.Waived, QualityLifecycle.Waived)]
    [InlineData(FindingState.FalsePositive, QualityLifecycle.FalsePositive)]
    [InlineData(FindingState.Resolved, QualityLifecycle.Resolved)]
    public void FindingStateMapsDeterministically(FindingState state, QualityLifecycle expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(state));

    [Theory]
    [InlineData("open", QualityLifecycle.Open)]
    [InlineData("accepted", QualityLifecycle.AcceptedRisk)]
    [InlineData("accepted-risk", QualityLifecycle.AcceptedRisk)]
    [InlineData("waived", QualityLifecycle.Waived)]
    [InlineData("false-positive", QualityLifecycle.FalsePositive)]
    [InlineData("falsePositive", QualityLifecycle.FalsePositive)]
    [InlineData("resolved", QualityLifecycle.Resolved)]
    public void LegacyLifecycleSpellingsIncludingTheDriftedCamelCaseFormResolve(string spelling, QualityLifecycle expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.TryParseLegacyLifecycleSpelling(spelling));

    [Fact]
    public void UnknownLifecycleSpellingReturnsNullInsteadOfGuessing() =>
        Assert.Null(QualityTaxonomyLegacyMapping.TryParseLegacyLifecycleSpelling("archived"));

    [Fact]
    public void ValidJsonEvidenceStringBecomesAToolResult()
    {
        const string evidence = """{"scanner":"gitleaks","matches":2}""";

        var mapping = QualityTaxonomyLegacyMapping.MapLegacyEvidenceString(evidence);

        Assert.Equal(QualityEvidenceKind.ToolResult, mapping.Kind);
        Assert.Equal("application/json", mapping.MediaType);
        Assert.Equal(evidence, mapping.PreservedContent);
        Assert.StartsWith("sha256:", mapping.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainTextEvidenceStringIsPreservedAsATypedDocument()
    {
        const string evidence = "Call site evidence";

        var mapping = QualityTaxonomyLegacyMapping.MapLegacyEvidenceString(evidence);

        Assert.Equal(QualityEvidenceKind.Document, mapping.Kind);
        Assert.Null(mapping.MediaType);
        Assert.Equal(evidence, mapping.PreservedContent);
        Assert.StartsWith("sha256:", mapping.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJsonLookingTextIsPreservedRatherThanDiscarded()
    {
        const string evidence = "{ not actually json";

        var mapping = QualityTaxonomyLegacyMapping.MapLegacyEvidenceString(evidence);

        Assert.Equal(QualityEvidenceKind.Document, mapping.Kind);
        Assert.Equal(evidence, mapping.PreservedContent);
    }
}
