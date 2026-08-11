using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static string RepositoryRoot => RepositoryTestContext.FindRepositoryRoot();

    [Fact]
    public void CoreCatalogueConformsAndPinsTheApprovedVocabulary()
    {
        using var catalogueJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-taxonomy.core.v1.json")));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryRoot, "schemas", "quality-taxonomy.v1.schema.json")));

        var result = schema.Evaluate(catalogueJson.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
        var catalogue = CoreQualityCatalogue.Instance;
        Assert.Equal("quality-studio/core", catalogue.Document.Id);
        Assert.Equal("1.0.0", catalogue.Document.Version);
        Assert.Matches("^sha256:[a-f0-9]{64}$", catalogue.Digest);
        Assert.True(catalogue.TryResolveTerm(
            CoreQualityTerms.Axes.Lifecycle, "accepted", out var accepted));
        Assert.Equal(CoreQualityTerms.Lifecycles.AcceptedRisk, accepted!.Id);
        Assert.True(catalogue.TryResolveTerm(
            CoreQualityTerms.Axes.Lifecycle, "falsePositive", out var falsePositive));
        Assert.Equal(CoreQualityTerms.Lifecycles.FalsePositive, falsePositive!.Id);
        Assert.True(catalogue.TryResolveAspect("dependencies", out var dependencies));
        Assert.Equal("security.dependencies", dependencies!.Id);
        Assert.True(catalogue.SupportsAspect("risk", CoreQualityTerms.Axes.Change));
        Assert.False(catalogue.SupportsAspect("risk", CoreQualityTerms.Axes.Assessment));
    }

    [Fact]
    public void ObservationSchemaAcceptsPositiveAndRejectsNegativeFixture()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryRoot, "schemas", "quality-observation.v1.schema.json")));
        using var valid = JsonDocument.Parse(ReadFixture("quality-observation.v1.valid.json"));
        using var invalid = JsonDocument.Parse(ReadFixture("quality-observation.v1.invalid.json"));

        var validResult = schema.Evaluate(valid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        var invalidResult = schema.Evaluate(invalid.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validResult.IsValid, validResult.ToString());
        Assert.False(invalidResult.IsValid);
    }

    [Fact]
    public void ObservationRoundTripsExplicitAndLegacyExtensions()
    {
        var document = QualityObservationJson.Deserialize(ReadFixture("quality-observation.v1.valid.json"));

        Assert.Equal("controlled",
            document.Extensions!["com.acme:comparison-label"].GetString());
        Assert.True(document.AdditionalProperties!["x-legacy-note"]
            .GetProperty("preserved").GetBoolean());

        using var roundTrip = JsonDocument.Parse(QualityObservationJson.Serialize(document));
        Assert.Equal("controlled", roundTrip.RootElement.GetProperty("extensions")
            .GetProperty("com.acme:comparison-label").GetString());
        Assert.True(roundTrip.RootElement.GetProperty("x-legacy-note")
            .GetProperty("preserved").GetBoolean());
    }

    [Fact]
    public void UnsupportedMajorIsQuarantinedWithoutDiscardingRawJson()
    {
        var json = ReadFixture("quality-observation.v1.valid.json")
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var result = QualityObservationJson.Read(json);

        Assert.False(result.Supported);
        Assert.Null(result.Document);
        Assert.Equal(2, result.SchemaVersion);
        Assert.True(result.Raw.GetProperty("x-legacy-note").GetProperty("preserved").GetBoolean());
        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));
        Assert.Contains("Unsupported quality observation schemaVersion '2'", exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownExtensionAspectRoundTripsButIsNotCoreAggregatable()
    {
        var json = ReadFixture("quality-observation.v1.valid.json")
            .Replace("code.correctness", "com.acme:resilience.backpressure", StringComparison.Ordinal);

        var document = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(document);

        Assert.Equal("com.acme:resilience.backpressure", Assert.Single(document.Aspects).AspectId);
        Assert.Contains("com.acme:resilience.backpressure", roundTrip, StringComparison.Ordinal);
        Assert.False(CoreQualityCatalogue.Instance.SupportsAspect(
            "com.acme:resilience.backpressure", CoreQualityTerms.Axes.Assessment));
    }

    [Theory]
    [InlineData("pass", "pass", "available", "allow")]
    [InlineData("warn", "concern", "available", "warn")]
    [InlineData("block", "fail", "available", "block")]
    [InlineData("unavailable", "inconclusive", "unavailable", "defer")]
    public void SecurityVerdictsMapAcrossSeparateAxes(
        string legacy, string assessment, string evidenceStatus, string decision)
    {
        var mapped = LegacyTaxonomyMapping.MapSecurityVerdict(legacy);

        Assert.Equal(CoreQualityTerms.Axes.Assessment, mapped.Axis);
        Assert.Equal(assessment, mapped.Value);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal(LegacyTaxonomyMapping.SecurityPolicyRef, mapped.PolicyRef);
    }

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("fail", "fail")]
    [InlineData("undetermined", "inconclusive")]
    public void FlowVerdictsMapDeterministically(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapping.MapFlowVerdict(legacy).Value);

    [Theory]
    [InlineData("pass", "pass")]
    [InlineData("finding", "fail")]
    [InlineData("not-applicable", "not-applicable")]
    [InlineData("not-yet-checked", "not-assessed")]
    public void AttackVerdictsMapDeterministically(string legacy, string expected) =>
        Assert.Equal(expected, LegacyTaxonomyMapping.MapAttackVerdict(legacy).Value);

    [Theory]
    [InlineData("no-quality-delta", "no-observed-delta")]
    [InlineData("improved", "improved")]
    [InlineData("neutral", "unchanged")]
    [InlineData("regression", "regressed")]
    public void ChangeSummariesMapOnTheChangeAxis(string legacy, string expected)
    {
        var mapped = LegacyTaxonomyMapping.MapChangeSummary(legacy);
        Assert.Equal(CoreQualityTerms.Axes.Change, mapped.Axis);
        Assert.Equal(expected, mapped.Value);
    }

    [Theory]
    [InlineData("good", "pass")]
    [InlineData("mixed", "concern")]
    [InlineData("concerning", "fail")]
    [InlineData("unknown", "inconclusive")]
    public void ChangeAspectVerdictsMapOnTheAssessmentAxis(string legacy, string expected)
    {
        var mapped = LegacyTaxonomyMapping.MapChangeAspect(legacy);
        Assert.Equal(CoreQualityTerms.Axes.Assessment, mapped.Axis);
        Assert.Equal(expected, mapped.Value);
    }

    [Theory]
    [InlineData("accepted", "accepted-risk")]
    [InlineData("accepted-risk", "accepted-risk")]
    [InlineData("falsePositive", "false-positive")]
    [InlineData("false-positive", "false-positive")]
    public void LifecycleAliasesMapWithoutLosingLegacySpellings(string legacy, string expected)
    {
        var mapped = LegacyTaxonomyMapping.MapLifecycle(legacy);
        Assert.Equal(CoreQualityTerms.Axes.Lifecycle, mapped.Axis);
        Assert.Equal(expected, mapped.Value);
    }

    [Fact]
    public void EvidenceMappingPreservesStructuredAndPlainTextPayloads()
    {
        var structured = LegacyTaxonomyMapping.MapEvidence("ev-json", "{\"tool\":\"gitleaks\"}");
        var text = LegacyTaxonomyMapping.MapEvidence("ev-text", "line 12 contains the relevant call");

        Assert.True(structured.ParsedAsJson);
        Assert.Equal(CoreQualityTerms.EvidenceKinds.ToolResult, structured.Evidence.Kind);
        Assert.Equal("application/json", structured.Evidence.MediaType);
        Assert.Equal("gitleaks", structured.Evidence.Raw!.Value.GetProperty("tool").GetString());
        Assert.False(text.ParsedAsJson);
        Assert.Equal(CoreQualityTerms.EvidenceKinds.Document, text.Evidence.Kind);
        Assert.Equal("text/plain", text.Evidence.MediaType);
        Assert.Equal("line 12 contains the relevant call", text.Evidence.Raw!.Value.GetString());
        Assert.Matches("^sha256:[a-f0-9]{64}$", structured.Evidence.ContentHash!);
    }

    [Fact]
    public void UnknownLegacyValueIsRejectedInsteadOfBeingCoerced()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyTaxonomyMapping.MapSecurityVerdict("maybe"));

        Assert.Contains("Unknown legacy security verdict", exception.Message, StringComparison.Ordinal);
    }

    private static string ReadFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "taxonomy", name));
}
