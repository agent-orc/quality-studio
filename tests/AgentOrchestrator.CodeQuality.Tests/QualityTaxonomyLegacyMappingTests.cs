using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingTests
{
    [Theory]
    [InlineData(SecurityVerdict.Pass, ObservationAssessment.Pass, ObservationEvidenceStatus.Available, "allow", null)]
    [InlineData(SecurityVerdict.Warn, ObservationAssessment.Concern, ObservationEvidenceStatus.Available, null, QualityTaxonomyLegacyMapping.SecuritySensorPolicyRef)]
    [InlineData(SecurityVerdict.Block, ObservationAssessment.Fail, ObservationEvidenceStatus.Available, "block", QualityTaxonomyLegacyMapping.SecuritySensorPolicyRef)]
    [InlineData(SecurityVerdict.Unavailable, ObservationAssessment.Inconclusive, ObservationEvidenceStatus.Unavailable, null, null)]
    public void Security_verdict_projects_onto_assessment_evidence_status_and_decision(
        SecurityVerdict verdict, ObservationAssessment assessment, ObservationEvidenceStatus evidenceStatus,
        string? decision, string? policyRef)
    {
        var projection = QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict);

        Assert.Equal(assessment, projection.Assessment);
        Assert.Equal(evidenceStatus, projection.EvidenceStatus);
        Assert.Equal(decision, projection.Decision);
        Assert.Equal(policyRef, projection.PolicyRef);
    }

    [Fact]
    public void Security_block_never_stores_the_policy_disposition_as_an_evidence_assessment()
    {
        var projection = QualityTaxonomyLegacyMapping.MapSecurityVerdict(SecurityVerdict.Block);

        Assert.NotEqual(projection.Decision, projection.Assessment.ToString().ToLowerInvariant());
        Assert.Equal("block", projection.Decision);
        Assert.Equal(ObservationAssessment.Fail, projection.Assessment);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, ObservationAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, ObservationAssessment.Inconclusive)]
    public void Flow_verdict_projects_onto_assessment(FlowReviewVerdict verdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowVerdict(verdict));

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, ObservationAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, ObservationAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, ObservationAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, ObservationAssessment.NotAssessed)]
    public void Attack_verdict_projects_onto_assessment(AttackCoverageVerdict verdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackVerdict(verdict));

    [Theory]
    [InlineData(ChangeReviewVerdict.NoQualityDelta, "no-observed-delta")]
    [InlineData(ChangeReviewVerdict.Improved, "improved")]
    [InlineData(ChangeReviewVerdict.Neutral, "unchanged")]
    [InlineData(ChangeReviewVerdict.Regression, "regressed")]
    public void Change_summary_projects_onto_the_change_axis(ChangeReviewVerdict summary, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeSummary(summary));

    [Theory]
    [InlineData("good", ObservationAssessment.Pass)]
    [InlineData("mixed", ObservationAssessment.Concern)]
    [InlineData("concerning", ObservationAssessment.Fail)]
    [InlineData("unknown", ObservationAssessment.Inconclusive)]
    public void Change_aspect_verdict_projects_onto_assessment(string verdict, ObservationAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(verdict));

    [Theory]
    [InlineData(FindingState.Open, "open")]
    [InlineData(FindingState.Accepted, "accepted-risk")]
    [InlineData(FindingState.Waived, "waived")]
    [InlineData(FindingState.FalsePositive, "false-positive")]
    [InlineData(FindingState.Resolved, "resolved")]
    public void Finding_state_projects_onto_the_lifecycle_axis(FindingState state, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(state));

    [Theory]
    [InlineData("open", "open")]
    [InlineData("falsePositive", "false-positive")]
    public void Flow_lifecycle_spelling_lands_on_the_same_term_as_finding_state(string flowSpelling, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowLifecycleState(flowSpelling));

    [Fact]
    public void Both_false_positive_spellings_converge_on_one_lifecycle_term()
    {
        Assert.Equal(
            QualityTaxonomyLegacyMapping.MapFindingState(FindingState.FalsePositive),
            QualityTaxonomyLegacyMapping.MapFlowLifecycleState("falsePositive"));
    }

    [Fact]
    public void Evidence_string_with_a_valid_json_payload_becomes_typed_tool_result_evidence()
    {
        const string evidence = """{"rule":"CWE-89","confidence":"high"}""";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", evidence);

        Assert.Equal(ObservationEvidenceKind.ToolResult, mapped.Kind);
        Assert.Equal("application/json", mapped.Locator["contentType"].GetString());
        Assert.Equal("CWE-89", mapped.Locator["payload"].GetProperty("rule").GetString());
        Assert.StartsWith("sha256:", mapped.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_string_with_plain_text_becomes_typed_document_evidence_preserving_the_original_text()
    {
        const string evidence = "Call site evidence: line 42 dereferences without a null check.";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", evidence);

        Assert.Equal(ObservationEvidenceKind.Document, mapped.Kind);
        Assert.Equal("text/plain", mapped.Locator["contentType"].GetString());
        Assert.Equal(evidence, mapped.Locator["text"].GetString());
    }

    [Fact]
    public void Evidence_string_mapping_is_deterministic()
    {
        var first = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", "same text");
        var second = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", "same text");

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Malformed_json_looking_text_is_preserved_as_document_evidence_not_discarded()
    {
        const string evidence = "{not valid json";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString("ev-1", evidence);

        Assert.Equal(ObservationEvidenceKind.Document, mapped.Kind);
        Assert.Equal(evidence, mapped.Locator["text"].GetString());
    }
}
