using Json.Schema;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void CoreCatalogue_validates_against_the_taxonomy_schema()
    {
        var json = QualityTaxonomyJson.Serialize(QualityTaxonomyCatalogue.CoreDocument);
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void CoreCatalogue_id_and_version_are_pinned()
    {
        Assert.Equal("quality-studio/core", QualityTaxonomyCatalogue.CoreCatalogueId);
        Assert.Equal("quality-studio/core", QualityTaxonomyCatalogue.CoreDocument.Id);
        Assert.Equal("1.0.0", QualityTaxonomyCatalogue.CoreDocument.Version);
        Assert.StartsWith("sha256:", QualityTaxonomyCatalogue.CoreDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void Digest_is_deterministic_across_loads()
    {
        var first = QualityTaxonomyJson.Digest(QualityTaxonomyCatalogue.CoreDocument);
        var second = QualityTaxonomyJson.Digest(QualityTaxonomyJson.Deserialize(QualityTaxonomyJson.Serialize(QualityTaxonomyCatalogue.CoreDocument)));

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("producerKind", "agent")]
    [InlineData("producerKind", "deterministic-sensor")]
    [InlineData("producerKind", "unknown")]
    [InlineData("evidenceStatus", "available")]
    [InlineData("evidenceStatus", "unavailable")]
    [InlineData("assessment", "pass")]
    [InlineData("assessment", "not-assessed")]
    [InlineData("change", "no-observed-delta")]
    [InlineData("decision", "block")]
    [InlineData("severity", "critical")]
    [InlineData("lifecycle", "accepted-risk")]
    [InlineData("lifecycle", "accepted")]
    [InlineData("lifecycle", "false-positive")]
    [InlineData("evidenceKind", "human-attestation")]
    public void Known_axis_terms_and_migration_aliases_are_recognized(string axis, string termOrAlias)
    {
        Assert.True(QualityTaxonomyCatalogue.IsKnownAxisTerm(axis, termOrAlias));
    }

    [Fact]
    public void Unknown_axis_term_is_not_recognized()
    {
        Assert.False(QualityTaxonomyCatalogue.IsKnownAxisTerm("assessment", "maybe"));
        Assert.False(QualityTaxonomyCatalogue.IsKnownAxisTerm("no-such-axis", "pass"));
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
    public void Legacy_aspect_aliases_resolve_to_the_namespaced_core_id(string legacyAlias, string coreId)
    {
        Assert.Equal(coreId, QualityTaxonomyCatalogue.ResolveAspectAlias(legacyAlias));
        Assert.True(QualityTaxonomyCatalogue.IsCoreAspect(coreId));
    }

    [Fact]
    public void Core_id_resolves_to_itself()
    {
        Assert.Equal("code.correctness", QualityTaxonomyCatalogue.ResolveAspectAlias("code.correctness"));
    }

    [Fact]
    public void Unrecognized_aspect_alias_resolves_to_null_instead_of_being_coerced()
    {
        Assert.Null(QualityTaxonomyCatalogue.ResolveAspectAlias("com.acme:resilience.backpressure"));
        Assert.False(QualityTaxonomyCatalogue.IsCoreAspect("com.acme:resilience.backpressure"));
    }

    [Fact]
    public void Catalogue_has_no_duplicate_aspect_ids()
    {
        var ids = QualityTaxonomyCatalogue.CoreDocument.Aspects.Select(aspect => aspect.Id).ToArray();

        Assert.Equal(ids.Distinct(StringComparer.Ordinal).Count(), ids.Length);
    }
}
