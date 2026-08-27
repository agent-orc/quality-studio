using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void BuiltInCatalogueValidatesAgainstTheSchema()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "src", "AgentOrchestrator.CodeQuality",
            "catalogues", "quality-taxonomy.core.v1.json"));
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void SchemaRejectsAnUnknownRootProperty()
    {
        var document = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "src", "AgentOrchestrator.CodeQuality",
                "catalogues", "quality-taxonomy.core.v1.json")));
        var withExtra = document.GetRawText().Replace(
            "\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"x-future\": true,", StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(withExtra);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Theory]
    [InlineData("correctness", "code.correctness")]
    [InlineData("code.correctness", "code.correctness")]
    [InlineData("boundaries", "security.boundary-exposure")]
    [InlineData("architecture-drift", "change.architecture-drift")]
    public void ResolverFindsAspectsByIdOrMigrationAlias(string idOrAlias, string expectedId)
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var resolved = resolver.TryResolveAspect(idOrAlias);

        Assert.NotNull(resolved);
        Assert.Equal(expectedId, resolved!.Id);
    }

    [Fact]
    public void ResolverReturnsNullForAnUnknownAspect()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        Assert.Null(resolver.TryResolveAspect("com.acme:resilience.backpressure"));
    }

    [Fact]
    public void ResolverFindsAxisTermsByAlias()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var term = resolver.TryResolveAxisTerm("lifecycle", "accepted");

        Assert.NotNull(term);
        Assert.Equal("accepted-risk", term!.Id);
    }

    [Fact]
    public void ClassifyAspectRecognizesACoreAspect()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var resolution = resolver.ClassifyAspect(resolver.Reference, "code.correctness");

        Assert.Equal(QualityTermResolution.Core, resolution);
    }

    [Fact]
    public void ClassifyAspectFlagsASameMajorUnrecognizedTermWithoutThrowing()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var resolution = resolver.ClassifyAspect(resolver.Reference, "com.acme:resilience.backpressure");

        Assert.Equal(QualityTermResolution.UnrecognizedTerm, resolution);
    }

    [Fact]
    public void ClassifyAspectQuarantinesAnUnknownMajorWithoutThrowing()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();
        var futureMajor = resolver.Reference with { Version = "2.0.0" };

        var resolution = resolver.ClassifyAspect(futureMajor, "code.correctness");

        Assert.Equal(QualityTermResolution.QuarantinedMajor, resolution);
    }

    [Fact]
    public void ClassifyAspectQuarantinesAForeignCatalogueId()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();
        var foreignCatalogue = resolver.Reference with { Id = "com.acme/extensions" };

        var resolution = resolver.ClassifyAspect(foreignCatalogue, "code.correctness");

        Assert.Equal(QualityTermResolution.QuarantinedMajor, resolution);
    }

    [Fact]
    public void ReferenceDigestIsStableAcrossLoads()
    {
        var first = new QualityTaxonomyCatalogueResolver().Reference;
        var second = new QualityTaxonomyCatalogueResolver().Reference;

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsDuplicateAspectIds()
    {
        var document = new QualityTaxonomyCatalogueDocument(
            "https://quality.studio/schemas/quality-taxonomy.v1.schema.json", 1, "quality-studio/core", "1.0.0",
            [new QualityTaxonomyAxis("severity", "Severity", [new QualityTaxonomyTerm("high", "High")])],
            [new QualityTaxonomyAspect("code.correctness", "Correctness"),
             new QualityTaxonomyAspect("code.correctness", "Correctness duplicate")]);

        Assert.Throws<JsonException>(() => new QualityTaxonomyCatalogueResolver(document));
    }

    [Fact]
    public void ConstructorRejectsDuplicateAliasesAcrossAspects()
    {
        var document = new QualityTaxonomyCatalogueDocument(
            "https://quality.studio/schemas/quality-taxonomy.v1.schema.json", 1, "quality-studio/core", "1.0.0",
            [new QualityTaxonomyAxis("severity", "Severity", [new QualityTaxonomyTerm("high", "High")])],
            [new QualityTaxonomyAspect("code.correctness", "Correctness", Aliases: ["shared"]),
             new QualityTaxonomyAspect("code.architecture", "Architecture", Aliases: ["shared"])]);

        Assert.Throws<JsonException>(() => new QualityTaxonomyCatalogueResolver(document));
    }
}
