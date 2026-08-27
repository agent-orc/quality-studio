using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityTaxonomyTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Lazy<JsonSchema> TaxonomySchema = new(() => LoadSchema("quality-taxonomy.v1.schema.json"));
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => LoadSchema("quality-observation.v1.schema.json"));

    [Fact]
    public void CoreCatalogueAndObservationFixturesValidateWhileNegativeFixturesFail()
    {
        var corePath = Path.Combine(
            RepositoryRoot,
            "src",
            "AgentOrchestrator.CodeQuality",
            "catalogues",
            "quality-studio-core.v1.json");
        AssertValid(TaxonomySchema.Value, corePath, true);
        AssertValid(ObservationSchema.Value, Fixture("quality-observation.v1.valid.json"), true);
        AssertValid(TaxonomySchema.Value, Fixture("quality-taxonomy.v1.invalid.json"), false);
        AssertValid(ObservationSchema.Value, Fixture("quality-observation.v1.invalid.json"), false);
    }

    [Fact]
    public void CoreCataloguePinsEveryApprovedAxisAndAspect()
    {
        var catalogue = QualityTaxonomyJson.LoadCoreCatalogue();

        Assert.Equal(QualityTaxonomyDocument.CoreId, catalogue.Id);
        Assert.Equal(QualityTaxonomyDocument.CoreVersion, catalogue.Version);
        Assert.All(catalogue.Terms, term =>
        {
            Assert.False(string.IsNullOrWhiteSpace(term.Description));
            Assert.False(term.Deprecated);
            Assert.NotEmpty(term.AllowedAxes);
        });

        AssertTerms(catalogue, QualityTaxonomyTermKind.ProducerKind,
            "agent", "deterministic-sensor", "human", "imported", "unknown");
        AssertTerms(catalogue, QualityTaxonomyTermKind.EvidenceStatus,
            "available", "partial", "unavailable");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Assessment,
            "pass", "concern", "fail", "inconclusive", "not-applicable", "not-assessed");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Change,
            "improved", "regressed", "mixed", "unchanged", "no-observed-delta", "inconclusive");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Decision, "allow", "warn", "block", "defer");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Severity, "critical", "high", "medium", "low", "info");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Lifecycle,
            "open", "accepted-risk", "waived", "false-positive", "resolved");
        AssertTerms(catalogue, QualityTaxonomyTermKind.EvidenceKind,
            "source-code", "test-result", "runtime-measurement", "tool-result", "artifact", "document",
            "human-attestation");
        AssertTerms(catalogue, QualityTaxonomyTermKind.Aspect,
            "code.correctness", "code.architecture", "security.general", "security.secrets",
            "security.dependencies", "security.authentication-authorization", "security.input-validation",
            "security.configuration-iac", "security.boundary-exposure", "security.business-logic",
            "security.attack-coverage", "performance.general", "change.risk", "change.test-evidence",
            "change.scope-discipline", "change.architecture-drift");

        Assert.Contains("accepted", catalogue.Terms.Single(term =>
            term.Kind == QualityTaxonomyTermKind.Lifecycle && term.Id == "accepted-risk").Aliases);
        Assert.Contains("falsePositive", catalogue.Terms.Single(term =>
            term.Kind == QualityTaxonomyTermKind.Lifecycle && term.Id == "false-positive").Aliases);
    }

    [Fact]
    public void ObservationPreservesExplicitAndLegacyExtensions()
    {
        var originalJson = File.ReadAllText(Fixture("quality-observation.v1.valid.json"));
        var observation = QualityObservationJson.Deserialize(originalJson);
        var serialized = QualityObservationJson.Serialize(observation);
        var roundTrip = QualityObservationJson.Deserialize(serialized);

        Assert.Equal(0.75, roundTrip.Aspects[1].Extensions!["com.acme:confidence"].GetDouble());
        Assert.Equal("batch-7", roundTrip.Extensions!["com.acme:review-batch"].GetString());
        Assert.True(roundTrip.LegacyExtensions["x-legacy-field"].GetProperty("preserved").GetBoolean());

        using var json = JsonDocument.Parse(serialized);
        var validation = ObservationSchema.Value.Evaluate(
            json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal("D", json.RootElement.GetProperty("aspects")[0].GetProperty("grade").GetProperty("band").GetString());
    }

    [Fact]
    public void CataloguePreservesExplicitExtensionData()
    {
        const string json = """
            {
              "$schema": "https://quality.studio/schemas/quality-taxonomy.v1.schema.json",
              "schemaVersion": 1,
              "id": "com.acme/quality",
              "version": "1.1.0",
              "terms": [{
                "id": "com.acme:resilience.backpressure",
                "kind": "aspect",
                "title": "Backpressure",
                "description": "Backpressure behavior under sustained load.",
                "order": 10,
                "aliases": [],
                "deprecated": false,
                "allowedAxes": ["assessment"],
                "extensions": { "com.acme:owner": "runtime-team" }
              }],
              "extensions": { "com.acme:reviewed": true }
            }
            """;

        var catalogue = QualityTaxonomyJson.Deserialize(json);
        var roundTrip = QualityTaxonomyJson.Deserialize(QualityTaxonomyJson.Serialize(catalogue));

        Assert.True(roundTrip.Extensions!["com.acme:reviewed"].GetBoolean());
        Assert.Equal("runtime-team", roundTrip.Terms[0].Extensions!["com.acme:owner"].GetString());
    }

    [Fact]
    public void UnknownExtensionAspectIsExcludedUntilItsCatalogueIsInstalled()
    {
        var observation = QualityObservationJson.Deserialize(
            File.ReadAllText(Fixture("quality-observation.v1.valid.json")));
        var core = QualityTaxonomyJson.LoadCoreCatalogue();
        var registry = new QualityTaxonomyRegistry([core]);

        Assert.True(registry.CanAggregateAspect(observation.Taxonomy, observation.Aspects[0].AspectId));
        Assert.False(registry.CanAggregateAspect(observation.ExtensionCatalogues[0], observation.Aspects[1].AspectId));

        var extension = new QualityTaxonomyDocument(
            QualityTaxonomyDocument.SchemaId,
            1,
            "com.acme/quality",
            "1.1.0",
            [
                new QualityTaxonomyTerm(
                    "com.acme:resilience.backpressure",
                    QualityTaxonomyTermKind.Aspect,
                    "Backpressure",
                    "Backpressure behavior under sustained load.",
                    10,
                    [],
                    false,
                    [QualityTaxonomyAxis.Assessment]),
            ]);

        registry = new QualityTaxonomyRegistry([core, extension]);
        Assert.True(registry.CanAggregateAspect(observation.ExtensionCatalogues[0], observation.Aspects[1].AspectId));
    }

    [Fact]
    public void UnknownMajorIsQuarantinedWithRawDataIntact()
    {
        const string json = """
            {
              "$schema": "https://quality.studio/schemas/quality-observation.v2.schema.json",
              "schemaVersion": 2,
              "observationId": "observation-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "futureFact": { "mustSurvive": true }
            }
            """;

        var result = QualityObservationJson.Read(json);

        Assert.Equal(QualityObservationReadStatus.UnsupportedSchemaVersion, result.Status);
        Assert.Null(result.Observation);
        Assert.True(result.RawDocument.GetProperty("futureFact").GetProperty("mustSurvive").GetBoolean());

        var exception = Assert.Throws<UnsupportedQualityObservationException>(() =>
            QualityObservationJson.Deserialize(json));
        Assert.Equal(2, exception.SchemaVersion);
        Assert.True(exception.RawDocument.GetProperty("futureFact").GetProperty("mustSurvive").GetBoolean());
    }

    private static JsonSchema LoadSchema(string fileName) => JsonSchema.FromText(
        File.ReadAllText(Path.Combine(RepositoryRoot, "schemas", fileName)));

    private static string Fixture(string fileName) => Path.Combine(
        RepositoryRoot,
        "tests",
        "AgentOrchestrator.CodeQuality.Tests",
        "Fixtures",
        "taxonomy",
        fileName);

    private static void AssertValid(JsonSchema schema, string path, bool expected)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.Equal(expected, result.IsValid);
    }

    private static void AssertTerms(
        QualityTaxonomyDocument catalogue,
        QualityTaxonomyTermKind kind,
        params string[] expected)
    {
        var actual = catalogue.Terms
            .Where(term => term.Kind == kind)
            .OrderBy(term => term.Order)
            .Select(term => term.Id);
        Assert.Equal(expected, actual);
    }
}
