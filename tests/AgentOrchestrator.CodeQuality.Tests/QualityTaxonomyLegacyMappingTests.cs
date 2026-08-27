namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// One assertion per row of docs/operations/data-model-taxonomy/index.html section 8
/// ("Compatibility mappings"). Every produced canonical value must also be a real term in the
/// shipped core catalogue, so a typo here or a catalogue/mapping drift fails loudly.
/// </summary>
public sealed class QualityTaxonomyLegacyMappingTests
{
    private static readonly ResolvedQualityTaxonomyCatalogue Catalogue =
        new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

    [Theory]
    [InlineData(SecurityVerdict.Pass, QualityObservationAssessment.Pass, QualityObservationEvidenceStatus.Available, "allow")]
    [InlineData(SecurityVerdict.Warn, QualityObservationAssessment.Concern, QualityObservationEvidenceStatus.Available, null)]
    [InlineData(SecurityVerdict.Block, QualityObservationAssessment.Fail, QualityObservationEvidenceStatus.Available, "block")]
    [InlineData(SecurityVerdict.Unavailable, QualityObservationAssessment.Inconclusive, QualityObservationEvidenceStatus.Unavailable, null)]
    public void Security_verdict_maps_to_assessment_evidence_status_and_decision(
        SecurityVerdict verdict, QualityObservationAssessment expectedAssessment,
        QualityObservationEvidenceStatus expectedEvidenceStatus, string? expectedDecision)
    {
        Assert.Equal(expectedAssessment, QualityTaxonomyLegacyMapping.MapSecurityVerdict(verdict));
        Assert.Equal(expectedEvidenceStatus, QualityTaxonomyLegacyMapping.MapSecurityEvidenceStatus(verdict));
        Assert.Equal(expectedDecision, QualityTaxonomyLegacyMapping.MapSecurityDecision(verdict));
        AssertKnownAssessment(expectedAssessment);
        AssertKnownTerm("evidenceStatus", expectedEvidenceStatus.ToString());
        if (expectedDecision is not null) Assert.True(Catalogue.HasTerm("decision", expectedDecision));
    }

    [Fact]
    public void Security_warn_retains_the_sensor_policy_ref()
    {
        Assert.Equal("security-sensor-agent-v1", QualityTaxonomyLegacyMapping.SecuritySensorPolicyRef);
    }

    [Theory]
    [InlineData(FlowReviewVerdict.Pass, QualityObservationAssessment.Pass)]
    [InlineData(FlowReviewVerdict.Fail, QualityObservationAssessment.Fail)]
    [InlineData(FlowReviewVerdict.Undetermined, QualityObservationAssessment.Inconclusive)]
    public void Flow_verdict_maps_to_assessment(FlowReviewVerdict verdict, QualityObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFlowVerdict(verdict));
        AssertKnownAssessment(expected);
    }

    [Theory]
    [InlineData(AttackCoverageVerdict.Pass, QualityObservationAssessment.Pass)]
    [InlineData(AttackCoverageVerdict.Finding, QualityObservationAssessment.Fail)]
    [InlineData(AttackCoverageVerdict.NotApplicable, QualityObservationAssessment.NotApplicable)]
    [InlineData(AttackCoverageVerdict.NotYetChecked, QualityObservationAssessment.NotAssessed)]
    public void Attack_verdict_maps_to_assessment(AttackCoverageVerdict verdict, QualityObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapAttackVerdict(verdict));
        AssertKnownAssessment(expected);
    }

    [Theory]
    [InlineData("no-quality-delta", "no-observed-delta")]
    [InlineData("improved", "improved")]
    [InlineData("neutral", "unchanged")]
    [InlineData("regression", "regressed")]
    public void Change_summary_maps_to_the_change_axis(string legacy, string expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeSummary(legacy));
        Assert.True(Catalogue.HasTerm("change", expected));
    }

    [Theory]
    [InlineData("good", QualityObservationAssessment.Pass)]
    [InlineData("mixed", QualityObservationAssessment.Concern)]
    [InlineData("concerning", QualityObservationAssessment.Fail)]
    [InlineData("unknown", QualityObservationAssessment.Inconclusive)]
    public void Change_aspect_verdict_maps_to_assessment(string legacy, QualityObservationAssessment expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapChangeAspectVerdict(legacy));
        AssertKnownAssessment(expected);
    }

    [Theory]
    [InlineData(FindingState.Open, "open")]
    [InlineData(FindingState.Accepted, "accepted-risk")]
    [InlineData(FindingState.Waived, "waived")]
    [InlineData(FindingState.FalsePositive, "false-positive")]
    [InlineData(FindingState.Resolved, "resolved")]
    public void Finding_state_maps_to_the_lifecycle_axis(FindingState state, string expected)
    {
        Assert.Equal(expected, QualityTaxonomyLegacyMapping.MapFindingState(state));
        Assert.True(Catalogue.HasTerm("lifecycle", expected));
    }

    [Fact]
    public void Finding_state_accepted_retains_its_legacy_spelling_as_a_read_alias()
    {
        var lifecycleAxis = Catalogue.Document.Axes.Single(axis => axis.Id == "lifecycle");
        var acceptedRisk = lifecycleAxis.Terms.Single(term => term.Id == "accepted-risk");
        var falsePositive = lifecycleAxis.Terms.Single(term => term.Id == "false-positive");

        Assert.Contains("accepted", acceptedRisk.Aliases ?? []);
        Assert.Contains("falsePositive", falsePositive.Aliases ?? []);
    }

    [Fact]
    public void Evidence_string_containing_valid_json_becomes_typed_tool_result_evidence()
    {
        var evidence = """{"cve":"CVE-2026-1","package":"left-pad"}""";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);

        Assert.Equal(QualityObservationEvidenceKind.ToolResult, mapped.Kind);
        Assert.Equal("application/json", mapped.MediaType);
        Assert.Equal(evidence, mapped.PreservedContent);
        Assert.StartsWith("sha256:", mapped.ContentHash, StringComparison.Ordinal);
        Assert.True(Catalogue.HasTerm("evidenceKind", "tool-result"));
    }

    [Fact]
    public void Evidence_string_containing_plain_text_becomes_typed_document_evidence()
    {
        var evidence = "Manual review noted a missing null check.";

        var mapped = QualityTaxonomyLegacyMapping.MapEvidenceString(evidence);

        Assert.Equal(QualityObservationEvidenceKind.Document, mapped.Kind);
        Assert.Null(mapped.MediaType);
        Assert.Equal(evidence, mapped.PreservedContent);
        Assert.True(Catalogue.HasTerm("evidenceKind", "document"));
    }

    [Fact]
    public void Evidence_string_mapping_is_deterministic()
    {
        var first = QualityTaxonomyLegacyMapping.MapEvidenceString("plain text");
        var second = QualityTaxonomyLegacyMapping.MapEvidenceString("plain text");

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    private static void AssertKnownAssessment(QualityObservationAssessment assessment) =>
        AssertKnownTerm("assessment", assessment.ToString());

    private static void AssertKnownTerm(string axisId, string pascalCaseValue)
    {
        var kebab = ToKebabCase(pascalCaseValue);
        Assert.True(Catalogue.HasTerm(axisId, kebab),
            $"Catalogue axis '{axisId}' is missing term '{kebab}' produced by the legacy mapping.");
    }

    private static string ToKebabCase(string pascalCase) =>
        string.Concat(pascalCase.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
