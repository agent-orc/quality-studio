using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", "quality-observation.v1.schema.json"))));

    public static TheoryData<LegacyQualityContract, string, string, string> LegacyMappings => new()
    {
        { LegacyQualityContract.SecurityVerdict, "pass", "assessment", "pass" },
        { LegacyQualityContract.SecurityVerdict, "warn", "assessment", "concern" },
        { LegacyQualityContract.SecurityVerdict, "block", "assessment", "fail" },
        { LegacyQualityContract.SecurityVerdict, "unavailable", "assessment", "inconclusive" },
        { LegacyQualityContract.FlowVerdict, "pass", "assessment", "pass" },
        { LegacyQualityContract.FlowVerdict, "fail", "assessment", "fail" },
        { LegacyQualityContract.FlowVerdict, "undetermined", "assessment", "inconclusive" },
        { LegacyQualityContract.AttackVerdict, "pass", "assessment", "pass" },
        { LegacyQualityContract.AttackVerdict, "finding", "assessment", "fail" },
        { LegacyQualityContract.AttackVerdict, "not-applicable", "assessment", "not-applicable" },
        { LegacyQualityContract.AttackVerdict, "not-yet-checked", "assessment", "not-assessed" },
        { LegacyQualityContract.ChangeSummary, "no-quality-delta", "change", "no-observed-delta" },
        { LegacyQualityContract.ChangeSummary, "improved", "change", "improved" },
        { LegacyQualityContract.ChangeSummary, "neutral", "change", "unchanged" },
        { LegacyQualityContract.ChangeSummary, "regression", "change", "regressed" },
        { LegacyQualityContract.ChangeAspect, "good", "assessment", "pass" },
        { LegacyQualityContract.ChangeAspect, "mixed", "assessment", "concern" },
        { LegacyQualityContract.ChangeAspect, "concerning", "assessment", "fail" },
        { LegacyQualityContract.ChangeAspect, "unknown", "assessment", "inconclusive" },
        { LegacyQualityContract.FindingState, "accepted", "lifecycle", "accepted-risk" },
        { LegacyQualityContract.FindingState, "falsePositive", "lifecycle", "false-positive" },
        { LegacyQualityContract.FindingState, "false-positive", "lifecycle", "false-positive" },
    };

    [Fact]
    public void EmbeddedCoreCatalogueConformsAndPinsAllCoreTerms()
    {
        var path = Path.Combine(RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-studio-core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var validation = TaxonomySchema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validation.IsValid, validation.ToString());
        var catalogue = QualityTaxonomyCatalogueStore.CoreCatalogue;
        Assert.Equal("quality-studio/core", catalogue.Id);
        Assert.Equal("1.0.0", catalogue.Version);
        Assert.Equal(8, catalogue.Axes.Count);
        Assert.Equal(16, catalogue.Aspects.Count);
        Assert.Matches("^sha256:[0-9a-f]{64}$", QualityTaxonomyCatalogueStore.CoreDigest);
        Assert.Equal(QualityTaxonomyTerms.Assessments,
            catalogue.Axes.Single(axis => axis.Id == "assessment").Terms.Select(term => term.Id).ToHashSet());
    }

    [Fact]
    public void PositiveAndNegativeObservationFixturesHaveExpectedConformance()
    {
        using var valid = JsonDocument.Parse(ReadFixture("quality-observation.v1.valid.json"));
        using var invalid = JsonDocument.Parse(ReadFixture("quality-observation.v1.invalid.json"));

        var validResult = ObservationSchema.Value.Evaluate(valid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var invalidResult = ObservationSchema.Value.Evaluate(invalid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validResult.IsValid, validResult.ToString());
        Assert.False(invalidResult.IsValid);
        Assert.Equal("fail", QualityObservationJson.Deserialize(valid.RootElement.GetRawText()).Assessment);
    }

    [Theory]
    [MemberData(nameof(LegacyMappings))]
    public void LegacyValuesMapDeterministically(
        LegacyQualityContract contract, string legacy, string expectedAxis, string expectedValue)
    {
        var first = LegacyQualityTaxonomyMapper.Map(contract, legacy);
        var second = LegacyQualityTaxonomyMapper.Map(contract, legacy);

        Assert.Equal(expectedAxis, first.Axis);
        Assert.Equal(expectedValue, first.Value);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SecurityMappingsKeepAssessmentAvailabilityAndPolicyOnSeparateAxes()
    {
        var unavailable = LegacyQualityTaxonomyMapper.Map(LegacyQualityContract.SecurityVerdict, "unavailable");
        var blocked = LegacyQualityTaxonomyMapper.Map(LegacyQualityContract.SecurityVerdict, "block");

        Assert.Equal("unavailable", unavailable.EvidenceStatus);
        Assert.Equal("inconclusive", unavailable.Value);
        Assert.Null(unavailable.Decision);
        Assert.Equal("block", blocked.Decision);
        Assert.Equal("security-sensor-agent-v1", blocked.PolicyRef);
    }

    [Fact]
    public void AspectAliasesAndAnalyzerFallbackAreExplicit()
    {
        Assert.Equal("code.correctness", LegacyQualityTaxonomyMapper.MapAspect("correctness"));
        Assert.Equal("security.dependencies",
            LegacyQualityTaxonomyMapper.MapAspect("analyzer", "dependency/nuget-vulnerability"));
        Assert.Equal("security.boundary-exposure",
            LegacyQualityTaxonomyMapper.MapAspect("analyzer", "boundary/missing-authorization"));
        Assert.Equal("quality.studio:sensor.analyzer", LegacyQualityTaxonomyMapper.MapAspect("analyzer"));
        Assert.Null(LegacyQualityTaxonomyMapper.MapAspect("third-party-term"));
    }

    [Fact]
    public void JsonAndTextEvidenceArePreservedWithTypedContentAndDigest()
    {
        var json = LegacyQualityTaxonomyMapper.MapEvidence("ev-json", "{\"result\":true}");
        var text = LegacyQualityTaxonomyMapper.MapEvidence("ev-text", "raw evidence");

        Assert.Equal("application/json", json.ContentType);
        Assert.True(json.Payload!.Value.GetProperty("result").GetBoolean());
        Assert.Equal("text/plain", text.ContentType);
        Assert.Equal("raw evidence", text.Payload!.Value.GetString());
        Assert.Matches("^sha256:[0-9a-f]{64}$", json.ContentHash);
    }

    [Fact]
    public void ExplicitExtensionsSurviveRoundTripAndUnknownTermsRemainVisible()
    {
        var source = ReadFixture("quality-observation.v1.valid.json")
            .Replace("code.correctness", "com.example:resilience.backpressure", StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(source);
        var serialized = QualityObservationJson.Serialize(loaded);
        var reloaded = QualityObservationJson.Deserialize(serialized);

        Assert.Equal("com.example:resilience.backpressure", Assert.Single(reloaded.Aspects).AspectId);
        Assert.Equal("EX-1", reloaded.Extensions!["com.example:review"].GetProperty("ticket").GetString());
        Assert.DoesNotContain(QualityTaxonomyCatalogueStore.CoreCatalogue.Aspects,
            aspect => aspect.Id == reloaded.Aspects[0].AspectId);
    }

    [Fact]
    public void UnknownObservationOrTaxonomyMajorsRetainRawJsonForQuarantine()
    {
        var source = ReadFixture("quality-observation.v1.valid.json");
        var unknownObservation = QualityObservationJson.Read(
            source.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        var unknownTaxonomy = QualityObservationJson.Read(
            source.Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal));

        Assert.False(unknownObservation.IsSupported);
        Assert.Equal(2, unknownObservation.Raw.GetProperty("schemaVersion").GetInt32());
        Assert.Contains("schemaVersion", unknownObservation.UnsupportedReason, StringComparison.Ordinal);
        Assert.False(unknownTaxonomy.IsSupported);
        Assert.Equal("quality-studio/core",
            unknownTaxonomy.Raw.GetProperty("taxonomy").GetProperty("id").GetString());
        Assert.Contains("taxonomy major", unknownTaxonomy.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPropertiesAreRejectedInsteadOfSilentlyDropped()
    {
        var source = ReadFixture("quality-observation.v1.valid.json")
            .Replace("\"assessment\": \"fail\",", "\"assessment\": \"fail\",\n  \"futureField\": 42,",
                StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(source));
    }

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", name));
}
