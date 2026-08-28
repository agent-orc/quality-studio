using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyCatalogueTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void BuiltInCatalogue_validates_against_the_schema()
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        using var parsed = JsonDocument.Parse(File.ReadAllText(path));

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void Resolver_loads_the_built_in_catalogue_with_all_required_axes()
    {
        var resolved = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.Equal("quality-studio/core", resolved.Id);
        Assert.Equal("1.0.0", resolved.Version);
        Assert.StartsWith("sha256:", resolved.Digest, StringComparison.Ordinal);
        Assert.True(resolved.IsCoreAspect("code.correctness"));
        Assert.True(resolved.IsCoreAspect("security.boundary-exposure"));
        Assert.False(resolved.IsCoreAspect("security.business-logic.unknown"));
    }

    [Fact]
    public void Resolver_is_deterministic_across_repeated_loads()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var first = resolver.ResolveBuiltIn();
        var second = resolver.ResolveBuiltIn();

        Assert.Equal(first.Digest, second.Digest);
    }

    [Theory]
    [InlineData("correctness", "code.correctness")]
    [InlineData("boundaries", "security.boundary-exposure")]
    [InlineData("architecture-drift", "change.architecture-drift")]
    public void Resolver_resolves_migration_aliases_to_core_ids(string alias, string expectedId)
    {
        var resolved = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.True(resolved.TryResolveAlias(alias, out var aspectId));
        Assert.Equal(expectedId, aspectId);
    }

    [Fact]
    public void Unknown_extension_aspect_is_not_a_core_aspect_and_has_no_alias()
    {
        var resolved = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        Assert.False(resolved.IsCoreAspect("com.acme:resilience.backpressure"));
        Assert.False(resolved.TryResolveAlias("com.acme:resilience.backpressure", out _));
    }

    [Fact]
    public void Resolver_rejects_a_catalogue_missing_a_required_axis()
    {
        var path = WriteTemporaryCatalogue(document => document.Replace(
            "\"evidenceKind\": {", "\"evidenceKindRenamed\": {", StringComparison.Ordinal));
        try
        {
            var exception = Assert.Throws<JsonException>(() => new QualityTaxonomyCatalogueResolver().ResolveFile(path));
            Assert.Contains("evidenceKind", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolver_rejects_a_catalogue_with_duplicate_aspect_ids()
    {
        var path = WriteTemporaryCatalogue(document => document.Replace(
            "\"security.business-logic\", \"title\": \"Business logic\" }",
            "\"code.correctness\", \"title\": \"Duplicate\" }",
            StringComparison.Ordinal));
        try
        {
            Assert.Throws<JsonException>(() => new QualityTaxonomyCatalogueResolver().ResolveFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Malformed_catalogue_fails_schema_validation_without_a_missing_field_slipping_through()
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        var mutated = File.ReadAllText(path).Replace("\"version\": \"1.0.0\",", string.Empty, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(mutated);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    private static string WriteTemporaryCatalogue(Func<string, string> mutate)
    {
        var sourcePath = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        var mutated = mutate(File.ReadAllText(sourcePath));
        var path = Path.Combine(Path.GetTempPath(), $"quality-taxonomy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, mutated);
        return path;
    }
}
