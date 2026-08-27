using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void FixtureValidatesAndRoundTrips()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));

        AssertSchemaValid(json);
        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);
        AssertSchemaValid(roundTrip);

        Assert.Equal("code.correctness", Assert.Single(observation.Aspects).AspectId);
        Assert.Equal(ObservationAssessment.Fail, observation.Assessment);
        Assert.Equal(ObservationProducerKind.Agent, observation.Producer.Kind);
        Assert.Equal("gpt-5.4-mini", observation.Producer.EffectiveModel);
    }

    [Fact]
    public void SerializerRoundTripsAndPreservesLegacyExtensionFields()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        var withFutureField = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);

        var observation = QualityObservationJson.Deserialize(withFutureField);

        Assert.NotNull(observation.LegacyExtensions);
        Assert.True(observation.LegacyExtensions!.ContainsKey("x-future-field"));

        var reserialized = QualityObservationJson.Serialize(observation);
        Assert.Contains("\"x-future-field\"", reserialized, StringComparison.Ordinal);
        AssertSchemaValid(reserialized);
    }

    [Fact]
    public void UnrecognizedNonLegacyPropertiesAreRejectedNotSilentlyDropped()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        var withUnknownField = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"unexpectedField\": true,",
            StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(withUnknownField));

        Assert.Contains("'x-' legacy prefix", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsAnUnsupportedSchemaMajorButLeavesRawJsonInspectable()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        var nextMajor = json.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);

        Assert.False(QualityObservationJson.IsSupportedMajor(nextMajor));
        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(nextMajor));
        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);

        using var stillParsable = JsonDocument.Parse(nextMajor);
        Assert.Equal(2, stillParsable.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void CoreAggregationExcludesUninstalledExtensionAspectsAndIncludesThemOnceInstalled()
    {
        var catalogue = QualityTaxonomyCatalogueResolver.ResolveCore();
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        var observation = QualityObservationJson.Deserialize(json) with
        {
            Aspects =
            [
                new ObservationAspect("code.correctness", ObservationAssessment.Fail, "Core aspect."),
                new ObservationAspect("com.acme:resilience.backpressure", ObservationAssessment.Concern, "Extension aspect."),
            ],
        };

        var withoutInstall = QualityObservationAggregation.CoreAspects(observation, catalogue);
        var unrecognizedWithoutInstall = QualityObservationAggregation.UnrecognizedAspects(observation, catalogue);
        var withInstall = QualityObservationAggregation.CoreAspects(observation, catalogue, ["com.acme"]);

        Assert.Equal(["code.correctness"], withoutInstall.Select(aspect => aspect.AspectId));
        Assert.Equal(["com.acme:resilience.backpressure"], unrecognizedWithoutInstall.Select(aspect => aspect.AspectId));
        Assert.Equal(2, withInstall.Count);
    }

    private static void AssertSchemaValid(string json)
    {
        using var document = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
