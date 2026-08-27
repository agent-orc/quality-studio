using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    private static readonly string FixedHash = "sha256:" + new string('a', 64);

    [Fact]
    public void A_populated_observation_conforms_to_the_repository_schema()
    {
        var observation = CreateObservation();

        var json = QualityObservationJson.Serialize(observation);

        using var document = JsonDocument.Parse(json);
        var result = ObservationSchema.Value.Evaluate(document.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void SerializerRoundTripsFieldOrderAndValues()
    {
        var observation = CreateObservation();

        var json = QualityObservationJson.Serialize(observation);
        var loaded = QualityObservationJson.Deserialize(json);

        Assert.Equal(observation.ObservationId, loaded.ObservationId);
        Assert.Equal(observation.Taxonomy, loaded.Taxonomy);
        Assert.Equal(observation.Producer, loaded.Producer);
        Assert.Single(loaded.Aspects);
        Assert.Single(loaded.Findings);
        Assert.Equal(json, QualityObservationJson.Serialize(loaded));
        Assert.True(json.IndexOf("\"$schema\"", StringComparison.Ordinal) <
                    json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) <
                    json.IndexOf("\"observationId\"", StringComparison.Ordinal));
    }

    [Fact]
    public void LoaderRejectsUnsupportedSchemaVersion()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extensions_survive_deserialize_and_serialize_and_are_excluded_from_core_aggregation()
    {
        var installed = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();
        var observation = CreateObservation() with
        {
            Aspects =
            [
                new QualityObservationAspect("code.correctness", QualityObservationAssessment.Pass, "Reviewed."),
                new QualityObservationAspect(
                    "com.acme:resilience.backpressure", QualityObservationAssessment.Concern, "Extension aspect."),
            ],
            Extensions = new Dictionary<string, JsonElement>
            {
                ["com.acme:resilience"] = JsonDocument.Parse(
                    "{\"version\":\"1.0.0\",\"digest\":\"sha256:" + new string('b', 64) + "\"}").RootElement.Clone(),
            },
        };

        var json = QualityObservationJson.Serialize(observation);
        var loaded = QualityObservationJson.Deserialize(json);

        Assert.NotNull(loaded.Extensions);
        Assert.True(loaded.Extensions!.ContainsKey("com.acme:resilience"));
        Assert.Equal(2, loaded.Aspects.Count);
        Assert.Contains(loaded.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");

        var coreAspects = QualityObservationCompatibility.CoreAspects(loaded, installed);

        Assert.Single(coreAspects);
        Assert.Equal("code.correctness", coreAspects[0].AspectId);
    }

    [Fact]
    public void Compatible_taxonomy_reference_is_reported_as_supported()
    {
        var installed = new QualityTaxonomyCatalogueResolver().ResolveBuiltIn();
        var observed = new QualityTaxonomyReference(installed.CatalogueId, installed.CatalogueVersion, installed.Digest);

        Assert.Equal(QualityTaxonomyCompatibility.Supported,
            QualityObservationCompatibility.Evaluate(observed, installed));
    }

    [Fact]
    public void ComputeObservationIdIsDeterministicAndSensitiveToEveryInput()
    {
        var baseline = QualityObservationJson.ComputeObservationId("run-1", "unit-1", "code", "sha256:aa", "sha256:bb", "sha256:cc");
        var repeat = QualityObservationJson.ComputeObservationId("run-1", "unit-1", "code", "sha256:aa", "sha256:bb", "sha256:cc");
        var differentRun = QualityObservationJson.ComputeObservationId("run-2", "unit-1", "code", "sha256:aa", "sha256:bb", "sha256:cc");

        Assert.Equal(baseline, repeat);
        Assert.NotEqual(baseline, differentRun);
        Assert.Matches("^observation-sha256:[a-f0-9]{64}$", baseline);
    }

    private static QualityObservationEnvelope CreateObservation()
    {
        var observationId = QualityObservationJson.ComputeObservationId(
            "review-run-1", "qs-v1/dotnet/file/src/A.cs", "code", FixedHash, FixedHash, FixedHash);
        return new QualityObservationEnvelope
        {
            ObservationId = observationId,
            Taxonomy = new QualityTaxonomyReference("quality-studio/core", "1.0.0", FixedHash),
            Subject = new QualityObservationSubject("qs-v1/dotnet/file/src/A.cs", FixedHash),
            Profile = new QualityObservationProfile("file-code-review", "1.0.0", FixedHash, FixedHash),
            Producer = new QualityObservationProducer(
                QualityObservationProducerKind.Agent, "review-run-1",
                Agent: "codex", Provider: "openai", RequestedModel: "gpt-5.4-mini", EffectiveModel: "gpt-5.4-mini",
                ThinkingLevel: "high", RoutePolicyVersion: "2026-07-24", ReviewRunId: "review-run-1"),
            EvidenceStatus = QualityObservationEvidenceStatus.Available,
            Evidence =
            [
                new QualityObservationEvidence(
                    "ev-1", QualityObservationEvidenceKind.SourceCode, "Unchecked value reaches the dereference.",
                    new QualityObservationEvidenceLocator("src/A.cs", "M:A.Run"), FixedHash),
            ],
            Aspects =
            [
                new QualityObservationAspect(
                    "code.correctness", QualityObservationAssessment.Fail, "One high-severity defect is evidenced.",
                    new QualityObservationGrade(68, "D")),
            ],
            Assessment = QualityObservationAssessment.Fail,
            Findings =
            [
                new QualityObservationFinding(
                    "of-1", "issue-1", FixedHash, "quality-studio-occurrence-v2",
                    "built-in/code.correctness.null-deref@1", "code.correctness", FindingSeverity.High,
                    ["ev-1"], new QualityObservationFindingSource(QualityObservationProducerKind.Agent, "self")),
            ],
        };
    }
}
