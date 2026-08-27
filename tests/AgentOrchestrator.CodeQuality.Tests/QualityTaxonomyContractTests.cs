using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    private static readonly Lazy<string> CoreCatalogueJson = new(() => File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "src", "AgentOrchestrator.CodeQuality", "catalogues",
        "quality-taxonomy-core.v1.json")));

    [Fact]
    public void Core_catalogue_file_validates_against_the_schema()
    {
        using var parsed = JsonDocument.Parse(CoreCatalogueJson.Value);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Core_catalogue_file_is_loadable_as_the_embedded_built_in_resource()
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        Assert.Equal(QualityTaxonomyCatalogue.CoreCatalogueId, catalogue.Id);
        Assert.Equal(QualityTaxonomyCatalogue.CoreCatalogueVersion, catalogue.Version);
        Assert.Contains(catalogue.Axes, axis => axis.Id == "severity");
        Assert.Contains(catalogue.Aspects, aspect => aspect.Id == "code.correctness");
    }

    [Theory]
    [InlineData("producer.kind", 5)]
    [InlineData("evidence.status", 3)]
    [InlineData("assessment", 6)]
    [InlineData("change", 6)]
    [InlineData("decision", 4)]
    [InlineData("severity", 5)]
    [InlineData("lifecycle", 5)]
    [InlineData("evidence.kind", 7)]
    public void Every_dossier_axis_is_pinned_with_its_exact_term_count(string axisId, int termCount)
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        var axis = catalogue.FindAxis(axisId);

        Assert.NotNull(axis);
        Assert.Equal(termCount, axis!.Terms.Count);
    }

    [Fact]
    public void Lifecycle_accepted_risk_retains_accepted_as_a_read_alias()
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        var resolved = catalogue.ResolveTerm("lifecycle", "accepted");

        Assert.NotNull(resolved);
        Assert.Equal("accepted-risk", resolved!.Value.Term.Id);
        Assert.True(resolved.Value.IsAlias);
    }

    [Fact]
    public void Unknown_term_does_not_resolve()
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        Assert.Null(catalogue.ResolveTerm("severity", "catastrophic"));
        Assert.Null(catalogue.ResolveTerm("no-such-axis", "pass"));
    }

    [Fact]
    public void Digest_is_deterministic_and_changes_with_content()
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        var first = catalogue.ComputeDigest();
        var second = catalogue.ComputeDigest();
        var mutated = catalogue with { Version = "1.0.1" };

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.NotEqual(first, mutated.ComputeDigest());
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.9.9", true)]
    [InlineData("2.0.0", false)]
    public void Major_compatibility_rejects_unknown_majors(string candidateVersion, bool supported)
    {
        var catalogue = QualityTaxonomyJson.LoadCore();

        var compatibility = QualityTaxonomyJson.EvaluateMajorCompatibility(catalogue, candidateVersion);

        Assert.Equal(supported, compatibility == QualityTaxonomyMajorCompatibility.Supported);
    }

    [Fact]
    public void Schema_rejects_an_axis_with_no_terms_without_needing_to_parse_it_as_the_contract_type()
    {
        const string malformed = """
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
              "schemaVersion": 1,
              "id": "quality-studio/core",
              "version": "1.0.0",
              "axes": [ { "id": "severity", "terms": [] } ],
              "aspects": []
            }
            """;
        using var parsed = JsonDocument.Parse(malformed);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void Schema_rejects_an_unversioned_catalogue_id()
    {
        const string malformed = """
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
              "schemaVersion": 1,
              "id": "quality-studio/core",
              "version": "not-a-semver",
              "axes": [ { "id": "severity", "terms": [ { "id": "high" } ] } ],
              "aspects": []
            }
            """;
        using var parsed = JsonDocument.Parse(malformed);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }
}
