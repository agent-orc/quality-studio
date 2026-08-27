using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    private static readonly Lazy<string> SampleJson = new(() => File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.sample.json")));

    [Fact]
    public void Sample_fixture_validates_and_round_trips_through_the_contract_type()
    {
        AssertSchemaValid(SampleJson.Value);

        var observation = QualityObservationJson.Deserialize(SampleJson.Value);
        var roundTrip = QualityObservationJson.Serialize(observation);

        AssertSchemaValid(roundTrip);
        Assert.Equal(ObservationProducerKind.Agent, observation.Producer.Kind);
        Assert.Equal(Assessment.Fail, observation.Assessment);
        Assert.Equal("code.correctness", Assert.Single(observation.Aspects).AspectId);
        Assert.Equal("ev-1", Assert.Single(Assert.Single(observation.Findings).EvidenceRefs));
    }

    [Fact]
    public void An_unknown_same_major_extension_term_round_trips_unchanged()
    {
        var withExtension = MutateAspectId(SampleJson.Value, "com.acme:resilience.backpressure");

        AssertSchemaValid(withExtension);
        var observation = QualityObservationJson.Deserialize(withExtension);
        var roundTrip = QualityObservationJson.Serialize(observation);

        using var roundTripDocument = JsonDocument.Parse(roundTrip);
        Assert.Equal("com.acme:resilience.backpressure",
            roundTripDocument.RootElement.GetProperty("aspects")[0].GetProperty("aspectId").GetString());
    }

    [Fact]
    public void An_unknown_taxonomy_major_still_round_trips_raw_data_without_being_treated_as_supported()
    {
        var withUnknownMajor = MutateTaxonomyVersion(SampleJson.Value, "2.0.0");

        AssertSchemaValid(withUnknownMajor);
        var observation = QualityObservationJson.Deserialize(withUnknownMajor);
        var core = QualityTaxonomyJson.LoadCore();

        Assert.Equal("2.0.0", observation.Taxonomy.Version);
        Assert.Equal(QualityTaxonomyMajorCompatibility.UnsupportedMajor,
            QualityTaxonomyJson.EvaluateMajorCompatibility(core, observation.Taxonomy.Version));
        var roundTrip = QualityObservationJson.Serialize(observation);
        AssertSchemaValid(roundTrip);
    }

    [Fact]
    public void Root_extensions_dictionary_survives_deserialize_and_serialize()
    {
        var withExtensions = AddRootExtensions(SampleJson.Value);

        AssertSchemaValid(withExtensions);
        var observation = QualityObservationJson.Deserialize(withExtensions);

        Assert.NotNull(observation.Extensions);
        Assert.True(observation.Extensions!.ContainsKey("com.acme:resilience"));

        var roundTrip = QualityObservationJson.Serialize(observation);
        using var roundTripDocument = JsonDocument.Parse(roundTrip);
        Assert.True(roundTripDocument.RootElement.GetProperty("extensions").TryGetProperty("com.acme:resilience", out _));
    }

    [Fact]
    public void Deserialize_rejects_a_document_with_the_wrong_schema_version()
    {
        var wrongVersion = SampleJson.Value.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(wrongVersion));
    }

    [Fact]
    public void Schema_rejects_a_document_missing_a_required_top_level_field()
    {
        using var document = JsonDocument.Parse(SampleJson.Value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("subject")) continue;
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var mutated = JsonDocument.Parse(stream.ToArray());
        var evaluation = Schema.Value.Evaluate(mutated.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    [Theory]
    [InlineData("evidenceStatus", "\"available\"", "\"partially-available\"")]
    [InlineData("assessment", "\"fail\"", "\"failed\"")]
    public void Schema_rejects_unknown_enum_spellings(string _, string validToken, string invalidToken)
    {
        var mutated = SampleJson.Value.Replace(validToken, invalidToken, StringComparison.Ordinal);

        using var parsed = JsonDocument.Parse(mutated);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    private static void AssertSchemaValid(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    private static string MutateAspectId(string json, string aspectId)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("aspects"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("aspects");
                writer.WriteStartArray();
                foreach (var aspect in property.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (var aspectProperty in aspect.EnumerateObject())
                    {
                        if (aspectProperty.NameEquals("aspectId"))
                        {
                            writer.WriteString("aspectId", aspectId);
                        }
                        else
                        {
                            aspectProperty.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MutateTaxonomyVersion(string json, string version)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("taxonomy"))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName("taxonomy");
                writer.WriteStartObject();
                foreach (var taxonomyProperty in property.Value.EnumerateObject())
                {
                    if (taxonomyProperty.NameEquals("version"))
                    {
                        writer.WriteString("version", version);
                    }
                    else
                    {
                        taxonomyProperty.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string AddRootExtensions(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WritePropertyName("extensions");
            writer.WriteStartObject();
            writer.WritePropertyName("com.acme:resilience");
            writer.WriteStartObject();
            writer.WriteString("backpressure", "observed");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
