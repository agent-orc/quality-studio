using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyCatalogueTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-taxonomy.v1.schema.json"))));

    private static readonly string[] ExpectedAxes =
    [
        "producerKind", "evidenceStatus", "assessment", "change", "decision",
        "severity", "lifecycle", "evidenceKind",
    ];

    private static readonly string[] ExpectedAspectIds =
    [
        "code.correctness", "code.architecture", "security.general", "security.secrets",
        "security.dependencies", "security.authentication-authorization", "security.input-validation",
        "security.configuration-iac", "security.boundary-exposure", "security.business-logic",
        "security.attack-coverage", "performance.general", "change.risk", "change.test-evidence",
        "change.scope-discipline", "change.architecture-drift",
    ];

    [Fact]
    public void CoreCatalogueValidatesAgainstTheTaxonomySchema()
    {
        var path = Path.Combine(RepositoryTestContext.FindRepositoryRoot(),
            "src", "AgentOrchestrator.CodeQuality", "catalogues", "quality-taxonomy.v1.core.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var evaluation = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void CoreCatalogueDeclaresEveryTargetAxisAndAspect()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.Equal("quality-studio/core", catalogue.Document.Id);
        Assert.Equal("1.0.0", catalogue.Document.Version);
        Assert.Equal(ExpectedAxes.OrderBy(id => id, StringComparer.Ordinal),
            catalogue.Document.Axes.Keys.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(ExpectedAspectIds.OrderBy(id => id, StringComparer.Ordinal),
            catalogue.Document.Aspects.Select(aspect => aspect.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("lifecycle", "open", "open")]
    [InlineData("lifecycle", "accepted", "accepted-risk")]
    [InlineData("lifecycle", "accepted-risk", "accepted-risk")]
    [InlineData("lifecycle", "falsePositive", "false-positive")]
    [InlineData("lifecycle", "false-positive", "false-positive")]
    [InlineData("producerKind", "unknown", "unknown")]
    public void TermAliasesResolveToTheCanonicalCoreTerm(string axis, string legacy, string expected)
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.True(catalogue.TryResolveTermAlias(axis, legacy, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void UnknownTermAliasDoesNotResolve()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.False(catalogue.TryResolveTermAlias("lifecycle", "archived", out _));
        Assert.False(catalogue.TryResolveTermAlias("no-such-axis", "open", out _));
    }

    [Theory]
    [InlineData("correctness", "code.correctness")]
    [InlineData("boundaries", "security.boundary-exposure")]
    [InlineData("architecture-drift", "change.architecture-drift")]
    public void AspectMigrationAliasesResolveToTheCoreAspectId(string legacyAspectId, string expectedCoreAspectId)
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.True(catalogue.TryResolveMigrationAlias(legacyAspectId, out var resolved));
        Assert.Equal(expectedCoreAspectId, resolved);
        Assert.True(catalogue.IsCoreAspect(resolved));
    }

    [Fact]
    public void UnknownAspectMigrationAliasDoesNotResolve()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.False(catalogue.TryResolveMigrationAlias("not-a-real-aspect", out _));
        Assert.False(catalogue.IsCoreAspect("com.acme:resilience.backpressure"));
    }

    [Fact]
    public void ResolutionIsDeterministicAndDigestIsStable()
    {
        var first = QualityTaxonomyCatalogueResolver.ResolveCore();
        var second = QualityTaxonomyCatalogueResolver.ResolveCore();

        Assert.Equal(first.Digest, second.Digest);
        Assert.Matches("^sha256:[a-f0-9]{64}$", first.Digest);
    }
}
