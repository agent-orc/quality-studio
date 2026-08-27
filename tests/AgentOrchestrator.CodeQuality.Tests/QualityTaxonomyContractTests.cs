using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    [Fact]
    public void CoreCatalogueValidatesAgainstItsSchema()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.LoadCore();
        var json = QualityTaxonomyJson.Serialize(catalogue);
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
        Assert.Equal(QualityTaxonomyCatalogue.CoreId, catalogue.Taxonomy.Id);
        Assert.Equal(QualityTaxonomyCatalogue.CoreVersion, catalogue.Taxonomy.Version);
    }

    [Fact]
    public void CoreCatalogueRoundTripsThroughSerialization()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.LoadCore();
        var json = QualityTaxonomyJson.Serialize(catalogue);

        var roundTripped = QualityTaxonomyJson.Deserialize(json);

        Assert.Equal(json, QualityTaxonomyJson.Serialize(roundTripped));
    }

    [Fact]
    public void EveryDossierAxisIsPresentWithItsExactTerms()
    {
        var axes = QualityTaxonomyCatalogueResolver.LoadCore().Axes;

        AssertTerms(axes.ProducerKind, "agent", "deterministic-sensor", "human", "imported", "unknown");
        AssertTerms(axes.EvidenceStatus, "available", "partial", "unavailable");
        AssertTerms(axes.Assessment, "pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed");
        AssertTerms(axes.Change, "improved", "regressed", "mixed", "unchanged", "no-observed-delta", "inconclusive");
        AssertTerms(axes.Decision, "allow", "warn", "block", "defer");
        AssertTerms(axes.Severity, "critical", "high", "medium", "low", "info");
        AssertTerms(axes.Lifecycle, "open", "accepted-risk", "waived", "false-positive", "resolved");
        AssertTerms(axes.EvidenceKind,
            "source-code", "test-result", "runtime-measurement", "tool-result", "artifact", "document", "human-attestation");
    }

    [Fact]
    public void LifecycleAliasesCoverTheDriftedLegacySpellings()
    {
        var lifecycle = QualityTaxonomyCatalogueResolver.LoadCore().Axes.Lifecycle;

        Assert.True(lifecycle.HasTerm("accepted"));
        Assert.True(lifecycle.HasTerm("falsePositive"));
    }

    [Theory]
    [InlineData("code.correctness")]
    [InlineData("code.architecture")]
    [InlineData("security.general")]
    [InlineData("security.secrets")]
    [InlineData("security.dependencies")]
    [InlineData("security.authentication-authorization")]
    [InlineData("security.input-validation")]
    [InlineData("security.configuration-iac")]
    [InlineData("security.boundary-exposure")]
    [InlineData("security.business-logic")]
    [InlineData("security.attack-coverage")]
    [InlineData("performance.general")]
    [InlineData("change.risk")]
    [InlineData("change.test-evidence")]
    [InlineData("change.scope-discipline")]
    [InlineData("change.architecture-drift")]
    public void CoreAspectCatalogueMatchesTheDossierInventory(string aspectId)
    {
        Assert.True(QualityTaxonomyCatalogueResolver.LoadCore().IsKnownAspect(aspectId));
    }

    [Theory]
    [InlineData("correctness")]
    [InlineData("architecture")]
    [InlineData("security")]
    [InlineData("dependencies")]
    [InlineData("boundaries")]
    [InlineData("performance")]
    [InlineData("risk")]
    public void MigrationAliasesResolveToCoreAspects(string legacyAspectId)
    {
        Assert.True(QualityTaxonomyCatalogueResolver.LoadCore().IsKnownAspect(legacyAspectId));
    }

    [Fact]
    public void UnknownExtensionAspectIsNotSilentlyCoercedIntoACoreTerm()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.LoadCore();

        Assert.False(catalogue.IsKnownAspect("com.acme:resilience.backpressure"));
    }

    [Fact]
    public void DigestChangesWhenCatalogueContentChanges()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.LoadCore();
        var digest = QualityTaxonomyJson.Digest(catalogue);

        var mutated = catalogue with
        {
            Aspects = [.. catalogue.Aspects, new QualityTaxonomyAspect("code.new-aspect", "Test")],
        };

        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);
        Assert.Equal(digest, QualityTaxonomyJson.Digest(catalogue));
        Assert.NotEqual(digest, QualityTaxonomyJson.Digest(mutated));
    }

    [Fact]
    public void DeserializeRejectsAnUnsupportedSchemaVersion()
    {
        var json = QualityTaxonomyJson.Serialize(QualityTaxonomyCatalogueResolver.LoadCore())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityTaxonomyJson.Deserialize(json));

        Assert.Contains("Unsupported quality taxonomy schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertTerms(QualityTaxonomyAxis axis, params string[] expectedIds)
    {
        Assert.Equal(expectedIds, axis.Terms.Select(term => term.Id));
    }
}
