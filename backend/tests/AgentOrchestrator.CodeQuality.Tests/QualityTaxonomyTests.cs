using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => SchemaAssert.Load("quality-taxonomy.v1.schema.json"));

    [Fact]
    public void CoreCatalogueValidatesAgainstItsPublishedSchema()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "backend", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy-core.v1.json")));

        var result = TaxonomySchema.Value.Evaluate(json.RootElement, SchemaAssert.Options);

        Assert.True(result.IsValid, SchemaAssert.Describe(result));
    }

    [Fact]
    public void CoreCatalogueIsIndependentlyVersionedAndDigested()
    {
        var core = QualityTaxonomyCatalogue.Core;

        Assert.Equal("quality-studio/core", core.Id);
        Assert.Equal("1.0.0", core.Version);
        Assert.Equal(1, core.MajorVersion);
        Assert.Null(core.Prefix);
        Assert.Matches("^sha256:[0-9a-f]{64}$", core.Digest);
        Assert.Equal(core.Digest, QualityTaxonomyCatalogue.Core.Digest);
    }

    [Theory]
    [InlineData("producerKind", "agent", "deterministic-sensor", "human", "imported", "unknown")]
    [InlineData("evidenceStatus", "available", "partial", "unavailable")]
    [InlineData("assessment", "pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed")]
    [InlineData("change", "improved", "regressed", "mixed", "unchanged", "no-observed-delta", "inconclusive")]
    [InlineData("decision", "allow", "warn", "block", "defer")]
    [InlineData("severity", "critical", "high", "medium", "low", "info")]
    [InlineData("lifecycle", "open", "accepted-risk", "waived", "false-positive", "resolved")]
    [InlineData("evidenceKind", "source-code", "test-result", "runtime-measurement", "tool-result", "artifact",
        "document", "human-attestation")]
    public void EachAxisPinsExactlyTheApprovedTerms(string axisId, params string[] expected)
    {
        Assert.True(QualityTaxonomyCatalogue.Core.TryGetAxis(axisId, out var axis));

        Assert.Equal(expected, axis.Terms.Select(term => term.Id));
    }

    [Theory]
    [InlineData("lifecycle", "accepted", "accepted-risk")]
    [InlineData("lifecycle", "falsePositive", "false-positive")]
    [InlineData("assessment", "not-yet-checked", "not-assessed")]
    [InlineData("assessment", "undetermined", "inconclusive")]
    [InlineData("producerKind", "deterministic", "deterministic-sensor")]
    [InlineData("evidenceKind", "sourceCode", "source-code")]
    public void DeclaredSpellingVariantsResolveToOneCanonicalTerm(string axis, string spelling, string canonical)
    {
        var resolved = QualityTaxonomyResolver.Default.ResolveTerm(axis, spelling);

        Assert.Equal(TaxonomyTermStatus.Alias, resolved.Status);
        Assert.Equal(canonical, resolved.CanonicalId);
        Assert.True(resolved.ParticipatesInCoreAggregation);
    }

    [Fact]
    public void SeverityKeepsItsCurrentOrdering()
    {
        var core = QualityTaxonomyCatalogue.Core;

        Assert.Equal(0, core.Rank("severity", "critical"));
        Assert.True(core.Rank("severity", "critical") < core.Rank("severity", "high"));
        Assert.True(core.Rank("severity", "low") < core.Rank("severity", "info"));
        Assert.Null(core.Rank("assessment", "pass"));
    }

    [Fact]
    public void AnUnknownTermStaysVisibleAndOutOfCoreAggregates()
    {
        var resolved = QualityTaxonomyResolver.Default.ResolveTerm("assessment", "mostly-fine");

        Assert.Equal(TaxonomyTermStatus.Unrecognized, resolved.Status);
        Assert.Null(resolved.CanonicalId);
        Assert.False(resolved.ParticipatesInCoreAggregation);
        Assert.Equal("unrecognized term", resolved.DisplayLabel);
        Assert.Equal("mostly-fine", resolved.Value);
    }

    [Fact]
    public void AspectIdsAreNamespacedAndKeepTheirMigrationAliases()
    {
        var resolver = QualityTaxonomyResolver.Default;

        Assert.Equal("code.correctness", resolver.ResolveAspect("correctness").CanonicalId);
        Assert.Equal("security.boundary-exposure", resolver.ResolveAspect("boundaries").CanonicalId);
        Assert.Equal("change.architecture-drift", resolver.ResolveAspect("architecture-drift").CanonicalId);
        Assert.Equal(TaxonomyTermStatus.Canonical, resolver.ResolveAspect("security.business-logic").Status);
        Assert.Equal(TaxonomyTermStatus.Unrecognized, resolver.ResolveAspect("sensor-availability").Status);
    }

    [Fact]
    public void EveryAliasIsUniqueAcrossTheAspectCatalogue()
    {
        var spellings = QualityTaxonomyCatalogue.Core.Aspects
            .SelectMany(aspect => (aspect.Aliases ?? []).Append(aspect.Id))
            .ToArray();

        Assert.Equal(spellings.Length, spellings.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ObjectBindingsDeclareWhichAxesMayAppearWhere()
    {
        var core = QualityTaxonomyCatalogue.Core;

        Assert.True(core.AxisAppliesTo("observation", "assessment"));
        Assert.True(core.AxisAppliesTo("observation", "evidenceStatus"));
        Assert.False(core.AxisAppliesTo("observation", "decision"));
        Assert.False(core.AxisAppliesTo("observation", "lifecycle"));
        Assert.True(core.AxisAppliesTo("policyOutcome", "decision"));
        Assert.True(core.AxisAppliesTo("lifecycleEvent", "lifecycle"));
        Assert.True(core.AxisAppliesTo("changeObservation", "change"));
        Assert.False(core.AxisAppliesTo("observationAspect", "change"));
    }

    [Fact]
    public void ExtensionTermsKeepTheirPrefixAndNeverRedefineACoreTerm()
    {
        var resolver = new QualityTaxonomyResolver(extensions: [QualityTaxonomyCatalogue.Load(ExtensionCatalogue)]);

        var extension = resolver.ResolveAspect("com.acme:resilience.backpressure");
        Assert.Equal(TaxonomyTermStatus.Extension, extension.Status);
        Assert.Equal("com.acme:resilience.backpressure", extension.CanonicalId);
        Assert.False(extension.ParticipatesInCoreAggregation);

        // The extension declares 'correctness' as an alias too; the bare spelling still resolves to core.
        Assert.Equal("code.correctness", resolver.ResolveAspect("correctness").CanonicalId);
        Assert.Equal(TaxonomyTermStatus.Canonical, resolver.ResolveTerm("assessment", "pass").Status);
        Assert.Equal(TaxonomyTermStatus.Extension, resolver.ResolveTerm("assessment", "com.acme:degraded").Status);
    }

    [Fact]
    public void AnExtensionTermOfAnUninstalledCatalogueIsUnrecognizedRatherThanCoerced()
    {
        var resolver = QualityTaxonomyResolver.Default;

        var aspect = resolver.ResolveAspect("com.acme:resilience.backpressure");
        var term = resolver.ResolveTerm("assessment", "com.acme:degraded");

        Assert.Equal(TaxonomyTermStatus.Unrecognized, aspect.Status);
        Assert.Equal(TaxonomyTermStatus.Unrecognized, term.Status);
        Assert.Null(term.CanonicalId);
    }

    [Fact]
    public void ExtensionCataloguesMustDeclareAUniquePrefix()
    {
        var extension = QualityTaxonomyCatalogue.Load(ExtensionCatalogue);

        Assert.Throws<ArgumentException>(() => new QualityTaxonomyResolver(extensions: [extension, extension]));
        Assert.Throws<ArgumentException>(() =>
            new QualityTaxonomyResolver(extensions: [QualityTaxonomyCatalogue.Core]));
    }

    [Fact]
    public void AnUnsupportedCatalogueMajorIsNotInterpreted()
    {
        var resolver = QualityTaxonomyResolver.Default;

        Assert.True(resolver.SupportsTaxonomy(QualityTaxonomyCatalogue.Core.Reference));
        Assert.True(resolver.SupportsTaxonomy(new("quality-studio/core", "1.7.3", "sha256:" + new string('a', 64))));
        Assert.False(resolver.SupportsTaxonomy(new("quality-studio/core", "2.0.0", "sha256:" + new string('a', 64))));
        Assert.False(resolver.SupportsTaxonomy(new("com.acme/core", "1.0.0", "sha256:" + new string('a', 64))));
    }

    [Theory]
    [InlineData("\"schemaVersion\": 1", "\"schemaVersion\": 2")]
    [InlineData("\"version\": \"1.0.0\"", "\"version\": \"1.0\"")]
    public void ACatalogueWithAnUnsupportedContractIsRejected(string original, string replacement)
    {
        var json = ExtensionCatalogue.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => QualityTaxonomyCatalogue.Load(json));
    }

    [Fact]
    public void ACatalogueWithADuplicateSpellingIsRejected()
    {
        var json = ExtensionCatalogue.Replace(
            "{ \"id\": \"degraded\", \"description\": \"The subject served traffic below its target.\" }",
            "{ \"id\": \"degraded\", \"description\": \"First.\" }, { \"id\": \"degraded\", \"description\": \"Second.\" }",
            StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityTaxonomyCatalogue.Load(json));

        Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
    }

    private const string ExtensionCatalogue = """
        {
          "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
          "schemaVersion": 1,
          "id": "com.acme/resilience",
          "version": "1.0.0",
          "prefix": "com.acme",
          "axes": [
            {
              "id": "assessment",
              "description": "Acme resilience outcomes.",
              "terms": [
                { "id": "degraded", "description": "The subject served traffic below its target." }
              ]
            }
          ],
          "objects": [
            { "id": "observation", "axes": ["assessment"] }
          ],
          "aspects": [
            {
              "id": "resilience.backpressure",
              "family": "resilience",
              "description": "Whether the subject sheds load safely.",
              "aliases": ["correctness"]
            }
          ]
        }
        """;
}

/// <summary>Shared JSON Schema assertions for the taxonomy and observation contracts.</summary>
internal static class SchemaAssert
{
    public static EvaluationOptions Options { get; } = new() { OutputFormat = OutputFormat.List };

    public static JsonSchema Load(string fileName) => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", fileName)));

    public static string Describe(EvaluationResults results) => results.ToString() ?? "invalid";
}
