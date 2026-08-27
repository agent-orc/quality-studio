using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => LoadSchema("quality-taxonomy.v1.schema.json"));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => LoadSchema("quality-observation.v1.schema.json"));

    [Fact]
    public void Core_catalogue_validates_and_pins_the_approved_vocabulary()
    {
        var path = Path.Combine(RepositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues",
            "quality-studio-core.v1.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));

        AssertValid(TaxonomySchema.Value, json.RootElement);
        var catalogue = QualityTaxonomy.LoadCoreCatalogue();

        Assert.Equal(QualityTaxonomy.Id, catalogue.Id);
        Assert.Equal(QualityTaxonomy.Version, catalogue.Version);
        Assert.Equal(
            ["producer-kind", "evidence-status", "assessment", "change", "decision", "severity", "lifecycle", "evidence-kind"],
            catalogue.Axes.OrderBy(axis => axis.Order).Select(axis => axis.Id));
        Assert.Equal(16, catalogue.Aspects.Count);
        Assert.All(catalogue.Axes.SelectMany(axis => axis.Terms), term =>
        {
            Assert.NotEmpty(term.Description);
            Assert.True(term.Order > 0);
        });
        Assert.All(catalogue.Aspects, aspect =>
        {
            Assert.NotEmpty(aspect.Description);
            Assert.NotEmpty(aspect.AllowedAssessmentAxes);
        });
        Assert.Contains(catalogue.Axes.Single(axis => axis.Id == "lifecycle").Terms,
            term => term.Id == "accepted-risk" && term.Aliases.Contains("accepted", StringComparer.Ordinal));
    }

    [Fact]
    public void Invalid_taxonomy_fixture_is_rejected()
    {
        using var json = JsonDocument.Parse(ReadFixture("quality-taxonomy.v1.invalid.json"));

        AssertInvalid(TaxonomySchema.Value, json.RootElement);
    }

    [Fact]
    public void Observation_fixture_validates_and_extensions_round_trip()
    {
        var source = ReadFixture("quality-observation.v1.valid.json");
        using var sourceJson = JsonDocument.Parse(source);
        AssertValid(ObservationSchema.Value, sourceJson.RootElement);

        var observation = QualityObservationJson.Deserialize(source);
        var serialized = QualityObservationJson.Serialize(observation);
        using var roundTrip = JsonDocument.Parse(serialized);

        AssertValid(ObservationSchema.Value, roundTrip.RootElement);
        Assert.True(roundTrip.RootElement.GetProperty("x-legacy-field").GetProperty("preserved").GetBoolean());
        Assert.Equal(2, roundTrip.RootElement.GetProperty("aspects")[1]
            .GetProperty("extensions").GetProperty("com.acme:weight").GetInt32());
    }

    [Fact]
    public void Invalid_observation_fixture_is_rejected()
    {
        using var json = JsonDocument.Parse(ReadFixture("quality-observation.v1.invalid.json"));

        AssertInvalid(ObservationSchema.Value, json.RootElement);
    }

    [Fact]
    public void Unknown_schema_or_taxonomy_major_is_unsupported_without_losing_raw_json()
    {
        var source = ReadFixture("quality-observation.v1.valid.json");
        var unknownSchema = QualityObservationJson.Read(source.Replace(
            "\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
        var unknownTaxonomy = QualityObservationJson.Read(source.Replace(
            "\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal));

        Assert.False(unknownSchema.IsSupported);
        Assert.Equal(2, unknownSchema.Raw.GetProperty("schemaVersion").GetInt32());
        Assert.False(unknownTaxonomy.IsSupported);
        Assert.Equal("2.0.0", unknownTaxonomy.Raw.GetProperty("taxonomy").GetProperty("version").GetString());
        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(
            source.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Unknown_extension_aspect_stays_visible_but_is_not_aggregatable_without_its_catalogue()
    {
        var observation = QualityObservationJson.Deserialize(ReadFixture("quality-observation.v1.valid.json"));
        var resolver = new QualityTaxonomyResolver(QualityTaxonomy.LoadCoreCatalogue());

        var selected = resolver.SelectAggregatableAspects(observation);

        Assert.Equal("code.correctness", Assert.Single(selected).AspectId);
        Assert.Contains(observation.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
    }

    [Theory]
    [InlineData("pass", "pass", "available", "allow")]
    [InlineData("warn", "concern", null, null)]
    [InlineData("block", "fail", null, "block")]
    [InlineData("unavailable", "inconclusive", "unavailable", null)]
    public void Security_verdict_mapping_is_deterministic(
        string legacy, string assessment, string? evidenceStatus, string? decision)
    {
        var mapped = LegacyQualityMapping.SecurityVerdict(legacy);

        Assert.Equal("assessment", mapped.Axis);
        Assert.Equal(assessment, mapped.Value);
        Assert.Equal(evidenceStatus, mapped.EvidenceStatus);
        Assert.Equal(decision, mapped.Decision);
        Assert.Equal(LegacyQualityMapping.SecurityPolicy, mapped.PolicyRef);
    }

    [Theory]
    [InlineData("flow", "pass", "assessment", "pass")]
    [InlineData("flow", "fail", "assessment", "fail")]
    [InlineData("flow", "undetermined", "assessment", "inconclusive")]
    [InlineData("attack", "pass", "assessment", "pass")]
    [InlineData("attack", "finding", "assessment", "fail")]
    [InlineData("attack", "not-applicable", "assessment", "not-applicable")]
    [InlineData("attack", "not-yet-checked", "assessment", "not-assessed")]
    [InlineData("change-summary", "no-quality-delta", "change", "no-observed-delta")]
    [InlineData("change-summary", "improved", "change", "improved")]
    [InlineData("change-summary", "neutral", "change", "unchanged")]
    [InlineData("change-summary", "regression", "change", "regressed")]
    [InlineData("change-aspect", "good", "assessment", "pass")]
    [InlineData("change-aspect", "mixed", "assessment", "concern")]
    [InlineData("change-aspect", "concerning", "assessment", "fail")]
    [InlineData("change-aspect", "unknown", "assessment", "inconclusive")]
    [InlineData("state", "accepted", "lifecycle", "accepted-risk")]
    [InlineData("state", "falsePositive", "lifecycle", "false-positive")]
    [InlineData("state", "false-positive", "lifecycle", "false-positive")]
    public void Legacy_mapping_vectors_match_the_approved_compatibility_table(
        string contract, string legacy, string axis, string value)
    {
        var mapped = contract switch
        {
            "flow" => LegacyQualityMapping.FlowVerdict(legacy),
            "attack" => LegacyQualityMapping.AttackVerdict(legacy),
            "change-summary" => LegacyQualityMapping.ChangeSummary(legacy),
            "change-aspect" => LegacyQualityMapping.ChangeAspect(legacy),
            "state" => LegacyQualityMapping.FindingState(legacy),
            _ => throw new InvalidOperationException(contract),
        };

        Assert.Equal(axis, mapped.Axis);
        Assert.Equal(value, mapped.Value);
    }

    [Fact]
    public void Legacy_evidence_preserves_json_and_plain_text()
    {
        var structured = LegacyQualityMapping.Evidence("{\"scanner\":\"gitleaks\"}");
        var text = LegacyQualityMapping.Evidence("line 4: possible secret");

        Assert.Equal("tool-result", structured.Kind);
        Assert.Equal("application/json", structured.MediaType);
        Assert.Equal("gitleaks", structured.Raw?.GetProperty("scanner").GetString());
        Assert.Equal("document", text.Kind);
        Assert.Equal("text/plain", text.MediaType);
        Assert.Equal("line 4: possible secret", text.Raw?.GetString());
        Assert.StartsWith("sha256:", structured.ContentHash, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_legacy_value_is_never_coerced()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyQualityMapping.FlowVerdict("maybe"));
    }

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryRoot, "schemas", fileName)));

    private static string ReadFixture(string fileName) => File.ReadAllText(Path.Combine(
        RepositoryRoot, "tests", "AgentOrchestrator.CodeQuality.Tests", "Fixtures", "Taxonomy", fileName));

    private static void AssertValid(JsonSchema schema, JsonElement json)
    {
        var result = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static void AssertInvalid(JsonSchema schema, JsonElement json)
    {
        var result = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid, result.ToString());
    }
}
