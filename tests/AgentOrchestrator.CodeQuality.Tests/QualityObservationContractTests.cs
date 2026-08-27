using Json.Schema;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void Fixture_validates_against_the_schema_and_round_trips()
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", "quality-observation.v1.json"));
        AssertValid(json);

        var observation = QualityObservationJson.Deserialize(json);
        var roundTrip = QualityObservationJson.Serialize(observation);

        AssertValid(roundTrip);
        Assert.Equal(QualityAssessment.Fail, observation.Assessment);
        Assert.Equal("code.correctness", Assert.Single(observation.Aspects).AspectId);
        Assert.Equal(FindingSeverity.High, Assert.Single(observation.Findings).Severity);
    }

    [Fact]
    public void Unknown_root_extension_field_survives_an_untouched_round_trip()
    {
        var original = CreateObservation();
        var json = QualityObservationJson.Serialize(original);
        var withFutureField = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(withFutureField);
        var reserialized = QualityObservationJson.Serialize(loaded);

        Assert.Contains("\"x-future-field\"", reserialized, StringComparison.Ordinal);
        Assert.Contains("\"enabled\": true", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_extensions_dictionary_survives_round_trip_and_is_not_a_core_aspect()
    {
        var original = CreateObservation() with
        {
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.acme:resilience"] = JsonSerializer.Deserialize<JsonElement>("""{"backpressure": "warn"}"""),
            },
        };

        var json = QualityObservationJson.Serialize(original);
        var loaded = QualityObservationJson.Deserialize(json);

        var extension = Assert.Single(loaded.Extensions!);
        Assert.Equal("com.acme:resilience", extension.Key);
        Assert.False(QualityTaxonomyCatalogue.IsCoreAspect(extension.Key));
    }

    [Fact]
    public void Deserialize_rejects_an_unsupported_schema_version()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_rejects_an_unsupported_taxonomy_major()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality taxonomy major", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_quarantines_an_unsupported_schema_version_without_discarding_raw_data()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 2,", StringComparison.Ordinal);

        var result = QualityObservationJson.Read(json);

        Assert.False(result.Supported);
        Assert.Null(result.Observation);
        Assert.NotNull(result.QuarantineReason);
        Assert.Equal("observation-" + new string('a', 64), result.RawDocument.GetProperty("observationId").GetString());
    }

    [Fact]
    public void Read_quarantines_an_unsupported_taxonomy_major_without_discarding_raw_data()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);

        var result = QualityObservationJson.Read(json);

        Assert.False(result.Supported);
        Assert.Null(result.Observation);
        Assert.Equal("2.0.0", result.RawDocument.GetProperty("taxonomy").GetProperty("version").GetString());
    }

    [Fact]
    public void Read_accepts_a_supported_document()
    {
        var json = QualityObservationJson.Serialize(CreateObservation());

        var result = QualityObservationJson.Read(json);

        Assert.True(result.Supported);
        Assert.NotNull(result.Observation);
        Assert.Null(result.QuarantineReason);
    }

    private static void AssertValid(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, JsonSerializer.Serialize(evaluation));
    }

    private static QualityObservationEnvelope CreateObservation() => new()
    {
        ObservationId = "observation-" + new string('a', 64),
        Taxonomy = new QualityTaxonomyRef("quality-studio/core", "1.0.0", "sha256:" + new string('b', 64)),
        Subject = new QualityObservationSubject(
            "qs-v1/dotnet/file/" + new string('c', 64),
            ManifestHash.Subject(new string('d', 64))),
        Profile = new QualityObservationProfile(
            "file-code-review", "1.0.0", "sha256:" + new string('e', 64), "sha256:" + new string('f', 64)),
        Producer = new QualityObservationProducer(
            QualityProducerKind.Agent, "gpt-5.4-mini", "gpt-5.4-mini", "high", "2026-07-24",
            "quality-run-1", "review-run-1", Agent: "codex", Provider: "openai"),
        EvidenceStatus = QualityEvidenceStatus.Available,
        Evidence =
        [
            new QualityEvidenceItem("ev-1", QualityEvidenceKind.SourceCode, "Unchecked value reaches the dereference.",
                new QualityEvidenceLocator("src/A.cs", "M:A.Run"), "sha256:" + new string('a', 64)),
        ],
        Aspects =
        [
            new QualityAspectObservation("code.correctness", QualityAssessment.Fail,
                "One high-severity defect is evidenced.", new QualityAspectGrade(68, GradeBand.D)),
        ],
        Assessment = QualityAssessment.Fail,
        Findings =
        [
            new QualityObservationFinding(
                "of-1", "sha256:" + new string('b', 64), "quality-studio-occurrence-v2",
                "built-in/code.correctness.null-deref@1", "code.correctness", FindingSeverity.High,
                ["ev-1"], new QualityObservationFindingSource(QualityProducerKind.Agent, "self"), IssueId: "issue-1"),
        ],
    };
}
