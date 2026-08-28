namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingsTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecision.Allow)]
    [InlineData(SecurityVerdict.Warn, QualityAssessment.Concern, QualityEvidenceStatus.Available, null)]
    [InlineData(SecurityVerdict.Block, QualityAssessment.Fail, QualityEvidenceStatus.Available, QualityDecision.Block)]
    [InlineData(SecurityVerdict.Unavailable, QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null)]
    public void MapSecurityVerdict_matches_the_dossier_compatibility_table(
        SecurityVerdict verdict, QualityAssessment expectedAssessment, QualityEvidenceStatus expectedEvidenceStatus,
        QualityDecision? expectedDecision)
    {
        var mapped = QualityTaxonomyLegacyMappings.MapSecurityVerdict(verdict);

        Assert.Equal(expectedAssessment, mapped.Assessment);
        Assert.Equal(expectedEvidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(expectedDecision, mapped.Decision);
    }

    [Fact]
    public void MapSecurityVerdict_never_emits_a_decision_without_a_policyRef()
    {
        foreach (var verdict in Enum.GetValues<SecurityVerdict>())
        {
            var mapped = QualityTaxonomyLegacyMappings.MapSecurityVerdict(verdict);
            if (mapped.Decision is not null) Assert.False(string.IsNullOrWhiteSpace(mapped.PolicyRef));
        }
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, QualityAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, QualityAssessment.Inconclusive)]
    public void MapFlowVerdict_matches_the_dossier_compatibility_table(FlowReviewVerdict verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapFlowVerdict(verdict));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, QualityAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, QualityAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, QualityAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, QualityAssessment.NotAssessed)]
    public void MapAttackVerdict_matches_the_dossier_compatibility_table(AttackCoverageVerdict verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapAttackVerdict(verdict));

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, QualityChange.NoObservedDelta)]
    [InlineData(ChangeReviewVerdict.Improved, QualityChange.Improved)]
    [InlineData(ChangeReviewVerdict.Neutral, QualityChange.Unchanged)]
    [InlineData(ChangeReviewVerdict.Regression, QualityChange.Regressed)]
    public void MapChangeSummary_matches_the_dossier_compatibility_table(ChangeReviewVerdict verdict, QualityChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapChangeSummary(verdict));

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void MapChangeAspectVerdict_matches_the_dossier_compatibility_table(string verdict, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapChangeAspectVerdict(verdict));

    [Theory]
    [InlineData(FindingState.Open, QualityLifecycleState.Open)]
    [InlineData(FindingState.Accepted, QualityLifecycleState.AcceptedRisk)]
    [InlineData(FindingState.Waived, QualityLifecycleState.Waived)]
    [InlineData(FindingState.FalsePositive, QualityLifecycleState.FalsePositive)]
    [InlineData(FindingState.Resolved, QualityLifecycleState.Resolved)]
    public void MapFindingState_matches_the_dossier_compatibility_table(FindingState state, QualityLifecycleState expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapFindingState(state));

    [Theory]
    [InlineData("accepted", QualityLifecycleState.AcceptedRisk)]
    [InlineData("false-positive", QualityLifecycleState.FalsePositive)]
    [InlineData("falsePositive", QualityLifecycleState.FalsePositive)]
    public void MapLifecycleSpelling_normalizes_drifted_spellings_from_F8(string spelling, QualityLifecycleState expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapLifecycleSpelling(spelling));

    [Fact]
    public void MapLegacyEvidenceString_returns_null_for_absent_evidence() =>
        Assert.Null(QualityTaxonomyLegacyMappings.MapLegacyEvidenceString(null));

    [Fact]
    public void MapLegacyEvidenceString_preserves_valid_json_as_tool_result_evidence()
    {
        const string evidence = """{"tool":"gitleaks","ruleId":"generic-api-key"}""";

        var mapped = QualityTaxonomyLegacyMappings.MapLegacyEvidenceString(evidence);

        Assert.NotNull(mapped);
        Assert.Equal(QualityEvidenceKind.ToolResult, mapped.Kind);
        Assert.True(mapped.ParsedAsJson);
        Assert.Equal(evidence, mapped.PreservedText);
        Assert.StartsWith("sha256:", mapped.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void MapLegacyEvidenceString_preserves_plain_text_as_document_evidence()
    {
        const string evidence = "Call site evidence: token is not validated before use.";

        var mapped = QualityTaxonomyLegacyMappings.MapLegacyEvidenceString(evidence);

        Assert.NotNull(mapped);
        Assert.Equal(QualityEvidenceKind.Document, mapped.Kind);
        Assert.False(mapped.ParsedAsJson);
        Assert.Equal(evidence, mapped.PreservedText);
    }

    [Fact]
    public void MapLegacyEvidenceString_is_deterministic_for_the_same_input()
    {
        const string evidence = """{"a":1}""";

        var first = QualityTaxonomyLegacyMappings.MapLegacyEvidenceString(evidence);
        var second = QualityTaxonomyLegacyMappings.MapLegacyEvidenceString(evidence);

        Assert.Equal(first, second);
    }
}
