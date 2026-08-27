using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void Built_in_core_catalogue_conforms_to_the_repository_schema()
    {
        var raw = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "src", "AgentOrchestrator.CodeQuality",
            "catalogues", "quality-taxonomy-core.v1.json"));
        using var parsed = JsonDocument.Parse(raw);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Core_catalogue_loads_with_a_stable_digest_and_expected_identity()
    {
        Assert.Equal("quality-studio/core", QualityTaxonomyCoreCatalogue.Document.Id);
        Assert.Equal("1.0.0", QualityTaxonomyCoreCatalogue.Document.Version);
        Assert.StartsWith("sha256:", QualityTaxonomyCoreCatalogue.Digest, StringComparison.Ordinal);
        Assert.Equal(QualityTaxonomyCoreCatalogue.Digest, QualityTaxonomyCoreCatalogue.Digest);

        var reference = QualityTaxonomyCoreCatalogue.Reference();
        Assert.Equal(QualityTaxonomyCoreCatalogue.Document.Id, reference.Id);
        Assert.Equal(QualityTaxonomyCoreCatalogue.Digest, reference.Digest);
    }

    [Theory]
    [InlineData("correctness", "code.correctness")]
    [InlineData("architecture", "code.architecture")]
    [InlineData("security", "security.general")]
    [InlineData("secrets", "security.secrets")]
    [InlineData("dependencies", "security.dependencies")]
    [InlineData("authentication-authorization", "security.authentication-authorization")]
    [InlineData("input-validation", "security.input-validation")]
    [InlineData("configuration-iac", "security.configuration-iac")]
    [InlineData("boundaries", "security.boundary-exposure")]
    [InlineData("performance", "performance.general")]
    [InlineData("risk", "change.risk")]
    [InlineData("test-evidence", "change.test-evidence")]
    [InlineData("scope-discipline", "change.scope-discipline")]
    [InlineData("architecture-drift", "change.architecture-drift")]
    public void Every_migration_alias_resolves_to_its_core_aspect(string legacyAlias, string coreAspectId)
    {
        var aspect = QualityTaxonomyCoreCatalogue.FindAspect(legacyAlias);

        Assert.NotNull(aspect);
        Assert.Equal(coreAspectId, aspect!.Id);
        Assert.Equal(aspect, QualityTaxonomyCoreCatalogue.FindAspect(coreAspectId));
    }

    [Fact]
    public void New_adapter_only_aspects_have_no_legacy_alias()
    {
        var businessLogic = QualityTaxonomyCoreCatalogue.FindAspect("security.business-logic");
        var attackCoverage = QualityTaxonomyCoreCatalogue.FindAspect("security.attack-coverage");

        Assert.NotNull(businessLogic);
        Assert.NotNull(attackCoverage);
        Assert.Null(businessLogic!.Aliases);
        Assert.Null(attackCoverage!.Aliases);
    }

    [Fact]
    public void Lifecycle_axis_declares_the_accepted_and_false_positive_aliases()
    {
        var acceptedRisk = QualityTaxonomyCoreCatalogue.FindTerm("lifecycle", "accepted");
        var falsePositive = QualityTaxonomyCoreCatalogue.FindTerm("lifecycle", "falsePositive");

        Assert.NotNull(acceptedRisk);
        Assert.Equal("accepted-risk", acceptedRisk!.Id);
        Assert.NotNull(falsePositive);
        Assert.Equal("false-positive", falsePositive!.Id);
    }

    [Fact]
    public void Unresolvable_alias_returns_null_instead_of_guessing()
    {
        Assert.Null(QualityTaxonomyCoreCatalogue.FindAspect("does-not-exist"));
        Assert.Null(QualityTaxonomyCoreCatalogue.FindTerm("severity", "does-not-exist"));
        Assert.Null(QualityTaxonomyCoreCatalogue.FindTerm("does-not-exist-axis", "high"));
    }

    [Fact]
    public void Fixture_with_an_unsupported_major_fails_schema_validation()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-taxonomy.invalid-major.v1.json"));
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void Reader_rejects_an_unsupported_major_without_discarding_the_raw_document()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-taxonomy.invalid-major.v1.json"));

        var exception = Assert.Throws<UnsupportedQualityTaxonomyMajorException>(() => QualityTaxonomyJson.Deserialize(json));

        Assert.Contains("\"quality-studio/core\"", exception.RawJson, StringComparison.Ordinal);
        using var rawParsed = JsonDocument.Parse(exception.RawJson);
        Assert.Equal(2, rawParsed.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Serialize_rejects_duplicate_axis_and_aspect_ids()
    {
        var duplicateAxes = new QualityTaxonomyCatalogueDocument(
            QualityTaxonomyCatalogueDocument.SchemaId, 1, "quality-studio/core", "1.0.0",
            [
                new QualityTaxonomyAxis("severity", [new QualityTaxonomyTerm("high", "High", "High impact.")]),
                new QualityTaxonomyAxis("severity", [new QualityTaxonomyTerm("low", "Low", "Low impact.")]),
            ],
            []);

        Assert.Throws<JsonException>(() => QualityTaxonomyJson.Serialize(duplicateAxes));
    }
}
