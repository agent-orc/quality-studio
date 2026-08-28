using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyLegacyMappingsTests
{
    [Theory]
    [InlineData("pass", QualityAssessment.Pass, QualityEvidenceStatus.Available, "allow", null)]
    [InlineData("warn", QualityAssessment.Concern, QualityEvidenceStatus.Available, null, "security-sensor-agent-v1")]
    [InlineData("block", QualityAssessment.Fail, QualityEvidenceStatus.Available, "block", "security-sensor-agent-v1")]
    [InlineData("unavailable", QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null, null)]
    public void Maps_every_security_verdict_deterministically(
        string legacy, QualityAssessment assessment, QualityEvidenceStatus evidenceStatus,
        string? decision, string? policyRef)
    {
        var mapping = QualityTaxonomyLegacyMappings.MapSecurityVerdict(legacy);

        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(decision, mapping.Decision);
        Assert.Equal(policyRef, mapping.PolicyRef);
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("fail", QualityAssessment.Fail)]
    [InlineData("undetermined", QualityAssessment.Inconclusive)]
    public void Maps_every_flow_verdict_deterministically(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapFlowVerdict(legacy));

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("finding", QualityAssessment.Fail)]
    [InlineData("not-applicable", QualityAssessment.NotApplicable)]
    [InlineData("not-yet-checked", QualityAssessment.NotAssessed)]
    public void Maps_every_attack_verdict_deterministically(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapAttackVerdict(legacy));

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void Maps_every_change_summary_deterministically(string legacy, QualityChange expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapChangeSummary(legacy));

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void Maps_every_change_aspect_verdict_deterministically(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapChangeAspect(legacy));

    [Theory]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    [InlineData("open", "open")]
    [InlineData("waived", "waived")]
    [InlineData("resolved", "resolved")]
    public void Maps_every_finding_state_deterministically(string legacy, string expected) =>
        Assert.Equal(expected, QualityTaxonomyLegacyMappings.MapFindingLifecycleState(legacy));

    [Fact]
    public void Valid_json_evidence_becomes_typed_tool_result_evidence()
    {
        var evidence = QualityTaxonomyLegacyMappings.MapEvidenceString("ev-1", """{"rule":"secret-detected","file":"a.env"}""");

        Assert.Equal(QualityEvidenceKind.ToolResult, evidence.Kind);
        Assert.Equal("application/json", evidence.Locator.GetProperty("mediaType").GetString());
        Assert.Equal("secret-detected", evidence.Locator.GetProperty("raw").GetProperty("rule").GetString());
        Assert.StartsWith("sha256:", evidence.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_evidence_keeps_the_fallback_kind_and_preserves_the_original_text()
    {
        var evidence = QualityTaxonomyLegacyMappings.MapEvidenceString(
            "ev-2", "Unchecked value reaches the dereference.", QualityEvidenceKind.SourceCode);

        Assert.Equal(QualityEvidenceKind.SourceCode, evidence.Kind);
        Assert.Equal("text/plain", evidence.Locator.GetProperty("mediaType").GetString());
        Assert.Equal("Unchecked value reaches the dereference.", evidence.Locator.GetProperty("text").GetString());
        Assert.StartsWith("sha256:", evidence.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_legacy_spellings_are_rejected_rather_than_silently_coerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMappings.MapSecurityVerdict("ok"));
        Assert.Throws<ArgumentOutOfRangeException>(() => QualityTaxonomyLegacyMappings.MapFindingLifecycleState("archived"));
    }
}
