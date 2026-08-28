using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void BuiltInCatalogueValidatesAgainstTaxonomySchema()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var catalogueText = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json"));
        using var document = JsonDocument.Parse(catalogueText);

        var result = TaxonomySchema.Value.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void ResolverLoadsBuiltInCatalogueAndComputesAStableDigest()
    {
        var resolver = new QualityTaxonomyCatalogueResolver();

        var first = resolver.ResolveBuiltIn();
        var second = resolver.ResolveBuiltIn();

        Assert.Equal("quality-studio/core", first.Id);
        Assert.Equal("1.0.0", first.Version);
        Assert.StartsWith("sha256:", first.Digest, StringComparison.Ordinal);
        Assert.Equal(first.Digest, second.Digest);
    }

    [Theory]
    [InlineData("code.correctness", "correctness")]
    [InlineData("code.architecture", "architecture")]
    [InlineData("security.general", "security")]
    [InlineData("security.secrets", "secrets")]
    [InlineData("security.dependencies", "dependencies")]
    [InlineData("security.authentication-authorization", "authentication-authorization")]
    [InlineData("security.input-validation", "input-validation")]
    [InlineData("security.configuration-iac", "configuration-iac")]
    [InlineData("security.boundary-exposure", "boundaries")]
    [InlineData("performance.general", "performance")]
    [InlineData("change.risk", "risk")]
    [InlineData("change.test-evidence", "test-evidence")]
    [InlineData("change.scope-discipline", "scope-discipline")]
    [InlineData("change.architecture-drift", "architecture-drift")]
    public void CoreAspectCatalogueCarriesEveryDossierMigrationAlias(string aspectId, string expectedAlias)
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        var aspect = taxonomy.FindAspect(aspectId);

        Assert.NotNull(aspect);
        Assert.Equal(expectedAlias, aspect!.MigrationAlias);
    }

    [Theory]
    [InlineData("security.business-logic")]
    [InlineData("security.attack-coverage")]
    public void NewAdapterAspectsHaveNoMigrationAliasByDesign(string aspectId)
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        var aspect = taxonomy.FindAspect(aspectId);

        Assert.NotNull(aspect);
        Assert.Null(aspect!.MigrationAlias);
    }

    [Fact]
    public void LifecycleAxisRetainsAcceptedAndFalsePositiveAsAliases()
    {
        var taxonomy = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();

        var lifecycle = taxonomy.FindAxis("lifecycle");

        Assert.NotNull(lifecycle);
        var acceptedRisk = Assert.Single(lifecycle!.Terms, term => term.Id == "accepted-risk");
        Assert.Contains("accepted", acceptedRisk.Aliases);
        var falsePositive = Assert.Single(lifecycle.Terms, term => term.Id == "false-positive");
        Assert.Contains("falsePositive", falsePositive.Aliases);
    }

    [Fact]
    public void HandWrittenObservationSampleValidatesAgainstSchemaAndLoads()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var sampleText = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "quality-observation.v1.sample.json"));
        using var sample = JsonDocument.Parse(sampleText);

        var result = ObservationSchema.Value.Evaluate(sample.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(result.IsValid, result.ToString());
        var loaded = QualityObservationJson.Deserialize(sampleText);
        Assert.Equal(QualityProducerKind.Agent, loaded.Producer.Kind);
        Assert.Equal(QualityAssessment.Fail, loaded.Assessment);
        Assert.Equal("code.correctness", Assert.Single(loaded.Aspects).AspectId);
    }

    [Fact]
    public void ObservationRejectsAnUnsupportedSchemaMajorInsteadOfDiscardingRawData()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var sampleText = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "quality-observation.v1.sample.json"))
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(sampleText));

        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
        // The raw document itself must remain inspectable even though typed loading refuses it.
        using var raw = JsonDocument.Parse(sampleText);
        Assert.Equal(2, raw.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void ObservationSchemaRejectsMissingRequiredProducerKind()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var sampleText = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "quality-observation.v1.sample.json"));
        using var sample = JsonDocument.Parse(sampleText);
        using var mutated = JsonDocument.Parse(sample.RootElement.GetRawText()
            .Replace("\"kind\": \"agent\",", string.Empty, StringComparison.Ordinal));

        var result = ObservationSchema.Value.Evaluate(mutated.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnknownRootExtensionSurvivesDeserializeSerializeRoundTrip()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        var sampleText = File.ReadAllText(Path.Combine(repositoryRoot, "samples", "quality-observation.v1.sample.json"));
        var withExtension = sampleText.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(withExtension);

        Assert.NotNull(loaded.Extensions);
        Assert.True(loaded.Extensions!.ContainsKey("x-future-field"));
        var roundTripped = QualityObservationJson.Serialize(loaded);
        using var roundTrippedDocument = JsonDocument.Parse(roundTripped);
        Assert.True(roundTrippedDocument.RootElement.TryGetProperty("x-future-field", out var extension));
        Assert.True(extension.GetProperty("enabled").GetBoolean());

        // Round-tripping through the schema still validates: unknown extensions do not break
        // conformance because the observation schema keeps additionalProperties open at the root.
        var result = ObservationSchema.Value.Evaluate(roundTrippedDocument.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        Assert.True(result.IsValid, result.ToString());
    }
}
