using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void Built_in_catalogue_file_validates_against_its_schema()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "src", "AgentOrchestrator.CodeQuality",
            "catalogues", "quality-taxonomy-core.v1.json"));
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Resolver_pins_the_catalogue_to_a_deterministic_digest()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var first = resolver.ResolveBuiltIn();
        var second = resolver.ResolveBuiltIn();

        Assert.Equal("quality-studio/core", first.Id);
        Assert.Equal("1.0.0", first.Version);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);
        Assert.Equal(first.Digest, second.Digest);
        Assert.Equal(8, first.Axes.Count);
        Assert.Equal(16, first.Aspects.Count);
    }

    [Fact]
    public void Lifecycle_axis_resolves_the_accepted_alias_to_accepted_risk()
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.True(taxonomy.TryGetTerm("lifecycle", "accepted", out var term));
        Assert.Equal("accepted-risk", term.Id);
        Assert.True(taxonomy.TryGetTerm("lifecycle", "accepted-risk", out var canonical));
        Assert.Same(term, canonical);
    }

    [Fact]
    public void Unknown_axis_or_term_is_reported_without_throwing()
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.False(taxonomy.TryGetTerm("assessment", "not-a-real-term", out _));
        Assert.False(taxonomy.TryGetTerm("not-a-real-axis", "pass", out _));
    }

    [Fact]
    public void Every_aspect_from_the_dossier_target_taxonomy_is_present()
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.True(taxonomy.TryGetAspect("code.correctness", out var correctness));
        Assert.Equal("correctness", correctness.MigrationAlias);
        Assert.True(taxonomy.TryGetAspect("security.business-logic", out var businessLogic));
        Assert.Null(businessLogic.MigrationAlias);
    }

    [Fact]
    public void A_different_major_version_is_reported_as_unknown_without_discarding_the_reference()
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.True(taxonomy.IsKnownMajor("quality-studio/core", "1.4.2"));
        Assert.False(taxonomy.IsKnownMajor("quality-studio/core", "2.0.0"));
        Assert.False(taxonomy.IsKnownMajor("some-extension/catalogue", "1.0.0"));
    }
}
