using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void FixtureValidatesAndRoundTrips()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        AssertValidJson(json);

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);

        AssertValidJson(roundTrip);
        Assert.Equal(QualityAssessment.Fail, observation.Assessment);
        Assert.Equal(QualityProducerKind.Agent, observation.Producer.Kind);
        Assert.Equal("code.correctness", Assert.Single(observation.Aspects).AspectId);
        Assert.Equal("of-1", Assert.Single(observation.Findings).ObservationFindingId);
    }

    [Fact]
    public void LoaderRejectsAnUnsupportedSchemaVersion()
    {
        var json = File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"))
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaRejectsAnObservationMissingRequiredFields()
    {
        using var minimal = JsonDocument.Parse("""{ "$schema": "https://quality.studio/schemas/quality-observation.v1.schema.json", "schemaVersion": 1 }""");

        var evaluation = Schema.Value.Evaluate(minimal.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void SchemaRejectsAnUnknownRootProperty()
    {
        var json = File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"))
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"x-future\": true,", StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void ExtensionsSurviveDeserializeAndSerializeUnchanged()
    {
        var json = File.ReadAllText(Path.Combine(
                RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"))
            .Replace("\"assessment\": \"fail\",\n  \"findings\"",
                "\"assessment\": \"fail\",\n  \"extensions\": { \"com.acme:note\": \"unmodeled but preserved\" },\n  \"findings\"",
                StringComparison.Ordinal);
        AssertValidJson(json);

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);

        Assert.NotNull(observation.Extensions);
        Assert.Equal("unmodeled but preserved", observation.Extensions!["com.acme:note"]!.GetValue<string>());
        AssertValidJson(roundTrip);
        Assert.Contains("\"com.acme:note\"", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedAspectIdRoundTripsWithoutJoiningTheCoreCatalogue()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        var observation = QualityObservationJson.Deserialize(json) with
        {
            Aspects = [new QualityAspectObservation
            {
                AspectId = "com.acme:resilience.backpressure",
                Assessment = QualityAssessment.Concern,
                Rationale = "Extension aspect not modeled by the installed core catalogue.",
            }],
        };
        AssertValidJson(QualityObservationJson.Serialize(observation));

        var resolver = new QualityTaxonomyCatalogueResolver();
        var resolution = resolver.ClassifyAspect(observation.Taxonomy, observation.Aspects[0].AspectId);

        Assert.Equal("com.acme:resilience.backpressure", observation.Aspects[0].AspectId);
        Assert.Equal(QualityTermResolution.UnrecognizedTerm, resolution);
    }

    private static void AssertValidJson(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
