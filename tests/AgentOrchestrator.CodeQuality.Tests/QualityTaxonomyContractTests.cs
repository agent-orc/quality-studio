using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void CoreCatalogueFileValidatesAgainstTheTaxonomySchema()
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var evaluation = TaxonomySchema.Value.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Theory]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":2,"id":"x","version":"1.0.0","axes":[{"id":"a","terms":[{"id":"t","title":"T","description":"D"}]}],"aspects":[]}""")]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":1,"id":"x","version":"not-a-semver","axes":[{"id":"a","terms":[{"id":"t","title":"T","description":"D"}]}],"aspects":[]}""")]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-taxonomy.v1.schema.json","schemaVersion":1,"id":"x","version":"1.0.0","axes":[],"aspects":[]}""")]
    public void MalformedCataloguesFailSchemaValidation(string json)
    {
        using var document = JsonDocument.Parse(json);

        var evaluation = TaxonomySchema.Value.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void BuiltInCoreCatalogueLoadsAndHashesDeterministically()
    {
        var first = QualityTaxonomyCatalogue.CoreDigest;
        var second = QualityTaxonomyCatalogue.CoreDigest;

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal(QualityTaxonomyCatalogue.CoreId, QualityTaxonomyCatalogue.CoreRef.Id);
        Assert.Equal(QualityTaxonomyCatalogue.CoreVersion, QualityTaxonomyCatalogue.CoreRef.Version);
        Assert.Equal(first, QualityTaxonomyCatalogue.CoreRef.Digest);
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.4.2", true)]
    [InlineData("2.0.0", false)]
    [InlineData("0.9.9", false)]
    public void SupportedCoreVersionChecksOnlyTheMajor(string version, bool expected)
    {
        Assert.Equal(expected, QualityTaxonomyCatalogue.IsSupportedCoreVersion(version));
    }

    [Theory]
    [InlineData("code.correctness", "correctness")]
    [InlineData("code.architecture", "architecture")]
    [InlineData("security.boundary-exposure", "boundaries")]
    [InlineData("change.architecture-drift", "architecture-drift")]
    public void CoreAspectsCarryTheirDocumentedMigrationAlias(string aspectId, string alias)
    {
        Assert.True(QualityTaxonomyCatalogue.AspectsById.ContainsKey(aspectId));
        Assert.Equal(aspectId, QualityTaxonomyCatalogue.AspectMigrationAliases[alias]);
    }

    [Fact]
    public void NewAdapterOnlyAspectsHaveNoMigrationAlias()
    {
        Assert.DoesNotContain("security.business-logic", QualityTaxonomyCatalogue.AspectMigrationAliases.Values);
        Assert.DoesNotContain("security.attack-coverage", QualityTaxonomyCatalogue.AspectMigrationAliases.Values);
        Assert.True(QualityTaxonomyCatalogue.AspectsById.ContainsKey("security.business-logic"));
        Assert.True(QualityTaxonomyCatalogue.AspectsById.ContainsKey("security.attack-coverage"));
    }
}
