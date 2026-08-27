using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void Built_in_core_catalogue_conforms_to_the_repository_schema()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json")));

        var result = TaxonomySchema.Value.Evaluate(catalogue.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Resolver_loads_the_built_in_catalogue_and_computes_a_stable_digest()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var first = resolver.ResolveBuiltIn();
        var second = resolver.ResolveBuiltIn();

        Assert.Equal("quality-studio/core", first.CatalogueId);
        Assert.Equal("1.0.0", first.CatalogueVersion);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);
        Assert.Equal(first.Digest, second.Digest);
        Assert.True(first.HasTerm("assessment", "pass"));
        Assert.True(first.HasAspect("code.correctness"));
        Assert.False(first.HasAspect("com.acme:resilience.backpressure"));
    }

    [Theory]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":2,"catalogueId":"x","catalogueVersion":"1.0.0","axes":[{"id":"a","title":"A","terms":[{"id":"t","title":"T"}]}],"aspects":[]}""")]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":1,"catalogueId":"x","catalogueVersion":"1.0.0","axes":[],"aspects":[]}""")]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":1,"catalogueId":"x","catalogueVersion":"1.0.0","axes":[{"id":"a","title":"A","terms":[{"id":"t","title":"T"},{"id":"t","title":"T2"}]}],"aspects":[]}""")]
    public void Malformed_catalogues_fail_schema_validation_or_resolver_validation(string json)
    {
        using var document = JsonDocument.Parse(json);
        var schemaResult = TaxonomySchema.Value.Evaluate(document.RootElement);

        if (schemaResult.IsValid)
        {
            var parsed = JsonSerializer.Deserialize<QualityTaxonomyCatalogueDocument>(json, QualityTaxonomyJson.Options)!;
            Assert.Throws<JsonException>(() => QualityTaxonomyCatalogueResolver.Validate(parsed, "test"));
        }
    }

    [Fact]
    public void An_unknown_taxonomy_major_is_reported_without_discarding_the_observation()
    {
        var installed = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();
        var observedFromNewerMajor = new QualityTaxonomyReference("quality-studio/core", "2.0.0", installed.Digest);

        var compatibility = QualityObservationCompatibility.Evaluate(observedFromNewerMajor, installed);

        Assert.Equal(QualityTaxonomyCompatibility.UnsupportedTaxonomyMajor, compatibility);
        Assert.Equal("quality-studio/core", observedFromNewerMajor.Id);
        Assert.Equal("2.0.0", observedFromNewerMajor.Version);
    }
}
