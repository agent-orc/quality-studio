namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class LegacyQualityTaxonomyMapperTests
{
    [Theory]
    [InlineData("pass", QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecision.Allow)]
    [InlineData("warn", QualityAssessment.Concern, null, QualityDecision.Warn)]
    [InlineData("block", QualityAssessment.Fail, null, QualityDecision.Block)]
    [InlineData("unavailable", QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null)]
    public void MapsEverySecurityVerdict(
        string legacy,
        QualityAssessment assessment,
        QualityEvidenceStatus? evidenceStatus,
        QualityDecision? decision)
    {
        var mapping = LegacyQualityTaxonomyMapper.MapSecurityVerdict(legacy);

        Assert.Equal(legacy, mapping.LegacyValue);
        Assert.Equal(assessment, mapping.Assessment);
        Assert.Equal(evidenceStatus, mapping.EvidenceStatus);
        Assert.Equal(decision, mapping.Decision?.Value);
        if (mapping.Decision is not null)
        {
            Assert.Equal(LegacyQualityTaxonomyMapper.SecurityPolicyRef, mapping.Decision.PolicyRef);
        }
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("fail", QualityAssessment.Fail)]
    [InlineData("undetermined", QualityAssessment.Inconclusive)]
    public void MapsEveryFlowVerdict(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMapper.MapFlowVerdict(legacy).Assessment);

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("finding", QualityAssessment.Fail)]
    [InlineData("not-applicable", QualityAssessment.NotApplicable)]
    [InlineData("not-yet-checked", QualityAssessment.NotAssessed)]
    public void MapsEveryAttackVerdict(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMapper.MapAttackVerdict(legacy).Assessment);

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void MapsEveryChangeSummary(string legacy, QualityChange expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMapper.MapChangeSummary(legacy).Change);

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void MapsEveryChangeAspect(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMapper.MapChangeAspect(legacy).Assessment);

    [Theory]
    [InlineData("open", QualityLifecycle.Open)]
    [InlineData("accepted", QualityLifecycle.AcceptedRisk)]
    [InlineData("accepted-risk", QualityLifecycle.AcceptedRisk)]
    [InlineData("waived", QualityLifecycle.Waived)]
    [InlineData("falsePositive", QualityLifecycle.FalsePositive)]
    [InlineData("false-positive", QualityLifecycle.FalsePositive)]
    [InlineData("resolved", QualityLifecycle.Resolved)]
    public void MapsEveryFindingStateSpelling(string legacy, QualityLifecycle expected)
    {
        var mapping = LegacyQualityTaxonomyMapper.MapFindingState(legacy);
        Assert.Equal(expected, mapping.Lifecycle);
        Assert.Equal(legacy, mapping.LegacyValue);
    }

    [Fact]
    public void MapsJsonEvidenceAsPreservedToolResult()
    {
        const string source = "{\"available\":false,\"reason\":\"scanner missing\"}";

        var evidence = LegacyQualityTaxonomyMapper.MapEvidence("ev-json", source);

        Assert.Equal(QualityEvidenceKind.ToolResult, evidence.Kind);
        Assert.Equal("application/json", evidence.MediaType);
        Assert.False(evidence.Payload!.Value.GetProperty("available").GetBoolean());
        Assert.StartsWith("sha256:", evidence.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void MapsPlainEvidenceAsVerbatimDocument()
    {
        const string source = "line 1\nnot json";

        var evidence = LegacyQualityTaxonomyMapper.MapEvidence("ev-text", source);

        Assert.Equal(QualityEvidenceKind.Document, evidence.Kind);
        Assert.Equal("text/plain", evidence.MediaType);
        Assert.Equal(source, evidence.Payload!.Value.GetString());
    }

    [Fact]
    public void UnknownLegacyValueIsRejectedInsteadOfCoerced() =>
        Assert.Throws<ArgumentException>(() => LegacyQualityTaxonomyMapper.MapFlowVerdict("maybe"));
}
