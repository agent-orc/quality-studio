using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Theory]
    [InlineData("quality-observation.v1.json")]
    [InlineData("quality-observation.with-extension.v1.json")]
    public void Fixtures_validate_and_round_trip(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", fixture));
        AssertSchemaValid(json);

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);

        AssertSchemaValid(roundTrip);
    }

    [Fact]
    public void Round_trip_preserves_the_typed_extensions_dictionary()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.with-extension.v1.json"));

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripJson = JsonDocument.Parse(roundTrip);

        Assert.NotNull(observation.Extensions);
        Assert.True(observation.Extensions!.ContainsKey("com.acme:resilience.backpressure"));
        var extensionAspect = Assert.Single(observation.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
        Assert.Equal(ObservationAssessment.Concern, extensionAspect.Assessment);
        Assert.True(roundTripJson.RootElement.GetProperty("extensions")
            .TryGetProperty("com.acme:resilience.backpressure", out _));
    }

    [Fact]
    public void Round_trip_preserves_unrecognized_root_properties_including_legacy_x_keys()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.with-extension.v1.json"));

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripJson = JsonDocument.Parse(roundTrip);

        Assert.NotNull(observation.LegacyExtensions);
        Assert.True(observation.LegacyExtensions!.ContainsKey("x-legacy-note"));
        Assert.Equal(
            "Imported from a pre-taxonomy sidecar.",
            roundTripJson.RootElement.GetProperty("x-legacy-note").GetString());
    }

    [Fact]
    public void Fixture_with_an_unsupported_major_fails_schema_validation()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.invalid-major.v1.json"));
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Fact]
    public void Reader_rejects_an_unsupported_major_without_discarding_the_raw_document()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.invalid-major.v1.json"));

        var exception = Assert.Throws<UnsupportedQualityObservationMajorException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("\"schemaVersion\": 2", exception.RawJson, StringComparison.Ordinal);
        using var rawParsed = JsonDocument.Parse(exception.RawJson);
        Assert.Equal("qs-v1/dotnet/file/src/C.cs", rawParsed.RootElement.GetProperty("subject").GetProperty("unitId").GetString());
    }

    [Fact]
    public void No_model_run_can_overwrite_another_models_observation_identity()
    {
        var first = Minimal(observationId: "observation-sha256:" + new string('1', 64), effectiveModel: "model-a");
        var second = Minimal(observationId: "observation-sha256:" + new string('2', 64), effectiveModel: "model-b");

        Assert.NotEqual(first.ObservationId, second.ObservationId);
        Assert.NotEqual(first.Producer.EffectiveModel, second.Producer.EffectiveModel);
        AssertSchemaValid(QualityObservationJson.Serialize(first));
        AssertSchemaValid(QualityObservationJson.Serialize(second));
    }

    private static QualityObservationEnvelope Minimal(string observationId, string effectiveModel) => new()
    {
        ObservationId = observationId,
        Taxonomy = QualityTaxonomyCoreCatalogue.Reference(),
        Subject = new QualityObservationSubject("qs-v1/dotnet/file/src/A.cs", "sha256:" + new string('3', 64)),
        Profile = new QualityObservationProfile(
            "file-code-review", "1.0.0", "sha256:" + new string('4', 64), "sha256:" + new string('5', 64)),
        Producer = new QualityObservationProducer(ObservationProducerKind.Agent, EffectiveModel: effectiveModel),
        EvidenceStatus = ObservationEvidenceStatus.Available,
        Evidence = [],
        Aspects = [new QualityObservationAspectAssessment("code.correctness", ObservationAssessment.Pass)],
        Assessment = ObservationAssessment.Pass,
        Findings = [],
    };

    private static void AssertSchemaValid(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
