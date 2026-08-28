using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Theory]
    [InlineData("quality-observation.agent.v1.json")]
    [InlineData("quality-observation.sensor.v1.json")]
    public void Fixtures_validate_and_round_trip(string fixture)
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", fixture));
        using var parsed = JsonDocument.Parse(json);
        AssertSchemaValid(parsed.RootElement);

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripJson = JsonDocument.Parse(roundTrip);
        AssertSchemaValid(roundTripJson.RootElement);
    }

    [Fact]
    public void Root_x_star_keys_survive_deserialize_and_serialize()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.agent.v1.json"));

        var observation = QualityObservationJson.Deserialize(json);

        Assert.NotNull(observation.LegacyExtensionData);
        Assert.True(observation.LegacyExtensionData!.TryGetValue("x-legacy-note", out var note));
        Assert.Equal("imported-from-review-meta-v2", note.GetString());

        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripJson = JsonDocument.Parse(roundTrip);
        Assert.Equal(
            "imported-from-review-meta-v2",
            roundTripJson.RootElement.GetProperty("x-legacy-note").GetString());
    }

    [Fact]
    public void Extensions_dictionary_survives_deserialize_and_serialize()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.sensor.v1.json"));

        var observation = QualityObservationJson.Deserialize(json);

        Assert.NotNull(observation.Extensions);
        Assert.True(observation.Extensions!.ContainsKey("com.acme:resilience.backpressure"));

        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripJson = JsonDocument.Parse(roundTrip);
        Assert.True(roundTripJson.RootElement.GetProperty("extensions")
            .TryGetProperty("com.acme:resilience.backpressure", out _));
    }

    [Fact]
    public void An_unsupported_schema_version_is_rejected()
    {
        var json = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.agent.v1.json"));
        var mutated = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(mutated));
    }

    [Theory]
    [InlineData("""{"$schema":"https://quality.studio/schemas/quality-observation.v1.schema.json","schemaVersion":1}""")]
    [InlineData("""
        {
          "$schema": "https://quality.studio/schemas/quality-observation.v1.schema.json",
          "schemaVersion": 1,
          "observationId": "observation-sha256:1111111111111111111111111111111111111111111111111111111111111111",
          "taxonomy": { "id": "quality-studio/core", "version": "1.0.0", "digest": "sha256:2222222222222222222222222222222222222222222222222222222222222222" },
          "subject": { "unitId": "unit-1", "manifestHash": "sha256:3333333333333333333333333333333333333333333333333333333333333333" },
          "profile": { "id": "file-code-review", "version": "1.0.0", "promptHash": "sha256:4444444444444444444444444444444444444444444444444444444444444444", "reviewInputsHash": "sha256:5555555555555555555555555555555555555555555555555555555555555555" },
          "producer": { "kind": "not-a-real-producer-kind" },
          "evidenceStatus": "available",
          "evidence": [],
          "aspects": [],
          "assessment": "pass",
          "findings": []
        }
        """)]
    public void Structurally_invalid_documents_fail_schema_validation(string invalidJson)
    {
        using var parsed = JsonDocument.Parse(invalidJson);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(evaluation.IsValid);
    }

    private static void AssertSchemaValid(JsonElement element)
    {
        var evaluation = Schema.Value.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
