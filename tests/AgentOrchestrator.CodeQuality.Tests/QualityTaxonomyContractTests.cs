using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => LoadSchema("quality-taxonomy.v1.schema.json"));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => LoadSchema("quality-observation.v1.schema.json"));

    [Fact]
    public void Core_catalogue_and_observation_fixture_conform_to_v1_schemas()
    {
        AssertSchemaValid(TaxonomySchema.Value, File.ReadAllText(Path.Combine(
            RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-taxonomy.core.v1.json")));
        AssertSchemaValid(ObservationSchema.Value, Fixture("quality-observation.v1.valid.json"));
    }

    [Fact]
    public void Negative_fixtures_are_rejected_by_v1_schemas()
    {
        AssertSchemaInvalid(TaxonomySchema.Value, Fixture("quality-taxonomy.v1.invalid.json"));
        AssertSchemaInvalid(ObservationSchema.Value, Fixture("quality-observation.v1.invalid.json"));
    }

    [Fact]
    public void Core_catalogue_pins_axes_terms_aliases_order_and_aspect_axes()
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        Assert.Equal(QualityTaxonomyCatalogueDocument.CoreId, catalogue.Id);
        Assert.Equal(QualityTaxonomyCatalogueDocument.CoreVersion, catalogue.Version);
        Assert.Equal(
            ["producer-kind", "evidence-status", "assessment", "change", "decision", "severity", "lifecycle", "evidence-kind"],
            catalogue.Axes.OrderBy(axis => axis.Order).Select(axis => axis.Id));
        Assert.Equal(16, catalogue.Aspects.Count);
        Assert.All(catalogue.Axes.SelectMany(axis => axis.Terms), term =>
        {
            Assert.NotEmpty(term.Title);
            Assert.NotEmpty(term.Description);
            Assert.False(term.Deprecated);
        });
        Assert.All(catalogue.Aspects, aspect =>
        {
            Assert.NotEmpty(aspect.Description);
            Assert.False(aspect.Deprecated);
            Assert.Equal(["assessment"], aspect.AllowedAxes);
        });
        Assert.Contains("accepted", Term(catalogue, "lifecycle", "accepted-risk").Aliases);
        Assert.Contains("falsePositive", Term(catalogue, "lifecycle", "false-positive").Aliases);
        Assert.Contains(catalogue.Aspects,
            aspect => aspect.Id == "security.dependencies" && aspect.Aliases.SequenceEqual(["dependencies"]));
    }

    [Fact]
    public void Explicit_and_legacy_extensions_survive_observation_round_trip()
    {
        var originalJson = Fixture("quality-observation.v1.valid.json");
        var read = QualityObservationJson.Read(originalJson);

        Assert.True(read.IsSupported);
        var observation = Assert.IsType<QualityObservationDocument>(read.Value);
        Assert.Equal(2, observation.Producer.Extensions!["com.acme:attempt"].GetInt32());
        Assert.True(observation.LegacyRootExtensions!["x-legacy-root"].GetProperty("preserved").GetBoolean());

        var serialized = QualityObservationJson.Serialize(observation);
        using var expected = JsonDocument.Parse(originalJson);
        using var actual = JsonDocument.Parse(serialized);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        AssertSchemaValid(ObservationSchema.Value, serialized);
    }

    [Fact]
    public void Unknown_major_is_quarantined_with_raw_json_intact()
    {
        const string future = """
            {
              "$schema": "https://quality.studio/schemas/quality-observation.v2.schema.json",
              "schemaVersion": 2,
              "futureResult": { "assessment": "excellent", "score": 101 }
            }
            """;

        var read = QualityObservationJson.Read(future);

        Assert.False(read.IsSupported);
        Assert.Null(read.Value);
        Assert.Contains("schemaVersion '2'", read.UnsupportedReason, StringComparison.Ordinal);
        Assert.Equal("excellent", read.Raw.GetProperty("futureResult").GetProperty("assessment").GetString());
        Assert.Equal(101, read.Raw.GetProperty("futureResult").GetProperty("score").GetInt32());
    }

    [Fact]
    public void Unknown_taxonomy_major_is_quarantined_with_raw_terms_intact()
    {
        const string future = """
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v2.schema.json",
              "schemaVersion": 2,
              "id": "quality-studio/core",
              "terms": [{ "id": "future-semantic", "meaning": { "preserve": true } }]
            }
            """;

        var read = QualityTaxonomyJson.Read(future);

        Assert.False(read.IsSupported);
        Assert.Null(read.Value);
        Assert.True(read.Raw.GetProperty("terms")[0].GetProperty("meaning").GetProperty("preserve").GetBoolean());
    }

    [Fact]
    public void Same_major_unknown_nested_fields_are_not_silently_discarded()
    {
        var exception = Assert.Throws<JsonException>(() =>
            QualityObservationJson.Read(Fixture("quality-observation.v1.invalid.json")));

        Assert.Contains("silentlyDropped", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_same_major_term_values_remain_visible_and_round_trip()
    {
        var json = Fixture("quality-observation.v1.valid.json")
            .Replace("\"assessment\": \"fail\"", "\"assessment\": \"com.acme:excellent\"",
                StringComparison.Ordinal);

        var read = QualityObservationJson.Read(json);
        var observation = Assert.IsType<QualityObservationDocument>(read.Value);

        Assert.Equal(new QualityTerm("com.acme:excellent"), observation.Assessment);
        Assert.Equal(new QualityTerm("com.acme:excellent"), Assert.Single(observation.Aspects).Assessment);
        using var expected = JsonDocument.Parse(json);
        using var actual = JsonDocument.Parse(QualityObservationJson.Serialize(observation));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        AssertSchemaValid(ObservationSchema.Value, actual.RootElement.GetRawText());
        Assert.False(new QualityTaxonomyRegistry().CanAggregate(
            "code.correctness", "assessment", "com.acme:excellent"));
    }

    [Fact]
    public void Extension_aspects_require_an_installed_catalogue_for_aggregation()
    {
        const string extensionAspect = "com.acme:resilience.backpressure";
        var coreOnly = new QualityTaxonomyRegistry();

        Assert.True(coreOnly.CanAggregate("code.correctness", "assessment"));
        Assert.True(coreOnly.CanAggregate("code.correctness", "assessment", "pass"));
        Assert.False(coreOnly.CanAggregate("code.correctness", "assessment", "excellent"));
        Assert.False(coreOnly.TryResolveAspect(extensionAspect, out _));
        Assert.False(coreOnly.CanAggregate(extensionAspect, "assessment"));

        var extension = new QualityTaxonomyCatalogueDocument
        {
            Id = "com.acme/quality",
            Version = "1.0.0",
            Description = "Acme quality extensions.",
            Axes = [],
            Aspects =
            [
                new QualityAspectDefinition
                {
                    Id = extensionAspect,
                    Title = "Backpressure",
                    Description = "Backpressure behavior under load.",
                    Order = 0,
                    Aliases = [],
                    Deprecated = false,
                    AllowedAxes = ["assessment"],
                },
            ],
        };

        var installed = new QualityTaxonomyRegistry([extension]);

        Assert.True(installed.TryResolveAspect(extensionAspect, out _));
        Assert.True(installed.CanAggregate(extensionAspect, "assessment"));
        Assert.True(installed.CanAggregate(extensionAspect, "assessment", "pass"));
        Assert.False(installed.CanAggregate(extensionAspect, "change"));
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass, null, QualityEvidenceStatus.Available)]
    [InlineData("warn", QualityAssessment.Concern, QualityDecision.Warn, QualityEvidenceStatus.Available)]
    [InlineData("block", QualityAssessment.Fail, QualityDecision.Block, QualityEvidenceStatus.Available)]
    [InlineData("unavailable", QualityAssessment.Inconclusive, null, QualityEvidenceStatus.Unavailable)]
    public void Security_verdict_conformance_vectors_are_deterministic(
        string legacy,
        QualityAssessment assessment,
        QualityDecision? decision,
        QualityEvidenceStatus evidenceStatus)
    {
        var mapped = LegacyQualityTaxonomyMappings.MapSecurityVerdict(legacy);

        Assert.Equal(assessment, mapped.Assessment);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(LegacyQualityTaxonomyMappings.SecurityPolicyRef, mapped.PolicyRef);
        Assert.Equal(legacy, mapped.LegacyValue);
    }

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("fail", QualityAssessment.Fail)]
    [InlineData("undetermined", QualityAssessment.Inconclusive)]
    public void Flow_verdict_conformance_vectors_are_deterministic(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMappings.MapFlowVerdict(legacy).Assessment);

    [Theory]
    [InlineData("pass", QualityAssessment.Pass)]
    [InlineData("finding", QualityAssessment.Fail)]
    [InlineData("not-applicable", QualityAssessment.NotApplicable)]
    [InlineData("not-yet-checked", QualityAssessment.NotAssessed)]
    public void Attack_verdict_conformance_vectors_are_deterministic(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMappings.MapAttackVerdict(legacy).Assessment);

    [Theory]
    [InlineData("no-quality-delta", QualityChange.NoObservedDelta)]
    [InlineData("improved", QualityChange.Improved)]
    [InlineData("neutral", QualityChange.Unchanged)]
    [InlineData("regression", QualityChange.Regressed)]
    public void Change_summary_conformance_vectors_are_deterministic(string legacy, QualityChange expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMappings.MapChangeSummary(legacy).Change);

    [Theory]
    [InlineData("good", QualityAssessment.Pass)]
    [InlineData("mixed", QualityAssessment.Concern)]
    [InlineData("concerning", QualityAssessment.Fail)]
    [InlineData("unknown", QualityAssessment.Inconclusive)]
    public void Change_aspect_conformance_vectors_are_deterministic(string legacy, QualityAssessment expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMappings.MapChangeAspect(legacy).Assessment);

    [Theory]
    [InlineData("open", QualityLifecycleState.Open)]
    [InlineData("accepted", QualityLifecycleState.AcceptedRisk)]
    [InlineData("accepted-risk", QualityLifecycleState.AcceptedRisk)]
    [InlineData("waived", QualityLifecycleState.Waived)]
    [InlineData("falsePositive", QualityLifecycleState.FalsePositive)]
    [InlineData("false-positive", QualityLifecycleState.FalsePositive)]
    [InlineData("resolved", QualityLifecycleState.Resolved)]
    public void Finding_state_conformance_vectors_are_deterministic(string legacy, QualityLifecycleState expected) =>
        Assert.Equal(expected, LegacyQualityTaxonomyMappings.MapFindingState(legacy).Lifecycle);

    [Fact]
    public void Evidence_string_vectors_preserve_json_and_plain_text_without_interpretation()
    {
        const string json = "{ \"scanner\": \"gitleaks\", \"count\": 2 }";
        const string text = "scanner output was malformed: {oops";

        var mappedJson = LegacyQualityTaxonomyMappings.MapEvidenceString(json);
        var mappedText = LegacyQualityTaxonomyMappings.MapEvidenceString(text);

        Assert.Equal(new QualityTerm("tool-result"), mappedJson.Kind);
        Assert.Equal("application/json", mappedJson.MediaType);
        Assert.Equal(json, mappedJson.OriginalText);
        Assert.Equal("gitleaks", mappedJson.RawContent!.Value.GetProperty("scanner").GetString());
        Assert.Equal("sha256:ceba88261c6f1b802d6cf4bb47393d8c8265a52498505ddae5cc4faf7a92465d", mappedJson.ContentHash);
        Assert.Equal(new QualityTerm("document"), mappedText.Kind);
        Assert.Equal("text/plain", mappedText.MediaType);
        Assert.Equal(text, mappedText.OriginalText);
        Assert.Null(mappedText.RawContent);
    }

    [Fact]
    public void Unknown_legacy_values_are_rejected_instead_of_coerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyQualityTaxonomyMappings.MapSecurityVerdict("success"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LegacyQualityTaxonomyMappings.MapFindingState("closed"));
    }

    private static QualityTaxonomyTermDefinition Term(
        QualityTaxonomyCatalogueDocument catalogue, string axisId, string termId) =>
        catalogue.Axes.Single(axis => axis.Id == axisId).Terms.Single(term => term.Id == termId);

    private static JsonSchema LoadSchema(string name) => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryRoot, "schemas", name)));

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(
        RepositoryRoot, "tests", "AgentOrchestrator.CodeQuality.Tests", "Fixtures", "DataModelTaxonomy", name));

    private static void AssertSchemaValid(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static void AssertSchemaInvalid(JsonSchema schema, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid, result.ToString());
    }
}
