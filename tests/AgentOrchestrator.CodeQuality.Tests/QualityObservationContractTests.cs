using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    private static QualityObservation CreateObservation() => new()
    {
        ObservationId = "observation-sha256:" + new string('a', 64),
        Taxonomy = QualityTaxonomyCatalogue.CoreRef,
        Subject = new ObservationSubject("qs-v1/dotnet/file/src/A.cs", "sha256:" + new string('b', 64)),
        Profile = new ObservationProfile("file-code-review", "1.0.0", "sha256:" + new string('c', 64), "sha256:" + new string('d', 64)),
        Producer = new ObservationProducer(
            TaxonomyProducerKind.Agent,
            Agent: "codex",
            Provider: "openai",
            RequestedModel: "gpt-5.4-mini",
            EffectiveModel: "gpt-5.4-mini",
            ThinkingLevel: "high",
            RoutePolicyVersion: "2026-07-24",
            RunId: "quality-run-1",
            ReviewRunId: "review-run-1"),
        EvidenceStatus = TaxonomyEvidenceStatus.Available,
        Evidence =
        [
            new ObservationEvidenceItem("ev-1", TaxonomyEvidenceKind.SourceCode, "Unchecked value reaches the dereference.",
                ContentHash: "sha256:" + new string('e', 64)),
        ],
        Aspects =
        [
            new ObservationAspectAssessment("code.correctness", TaxonomyAssessment.Fail,
                "One high-severity defect is evidenced.", new ObservationAspectGrade(68, "D")),
        ],
        Assessment = TaxonomyAssessment.Fail,
        Findings =
        [
            new ObservationFinding("of-1", "issue-1", "sha256:" + new string('f', 64), "quality-studio-occurrence-v2",
                "code.correctness", FindingSeverity.High, ["ev-1"], new ObservationFindingSource(TaxonomyProducerKind.Agent, "self"),
                RuleRef: "built-in/code.correctness.null-deref@1"),
        ],
    };

    [Fact]
    public void ExampleObservationValidatesAndRoundTrips()
    {
        var original = CreateObservation();

        var json = QualityObservationJson.Serialize(original);
        using var parsed = JsonDocument.Parse(json);
        var evaluation = ObservationSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        Assert.True(evaluation.IsValid, evaluation.ToString());

        var loaded = QualityObservationJson.Deserialize(json);
        Assert.Equal(original.ObservationId, loaded.ObservationId);
        Assert.Equal(original.Taxonomy, loaded.Taxonomy);
        Assert.Equal(original.Assessment, loaded.Assessment);
        Assert.Equal(json, QualityObservationJson.Serialize(loaded));
    }

    [Fact]
    public void RootExtensionAndLegacyXPrefixFieldsSurviveRoundTrip()
    {
        var json = QualityObservationJson.Serialize(CreateObservation());
        var withExtensions = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"extensions\": { \"com.acme:resilience\": { \"backpressure\": \"observed\" } },\n  \"x-vendor-note\": \"kept\",",
            StringComparison.Ordinal);

        var loaded = QualityObservationJson.Deserialize(withExtensions);
        var roundTrip = QualityObservationJson.Serialize(loaded);

        Assert.NotNull(loaded.Extensions);
        Assert.True(loaded.Extensions!.ContainsKey("extensions"));
        Assert.True(loaded.Extensions!.ContainsKey("x-vendor-note"));
        Assert.Contains("\"x-vendor-note\": \"kept\"", roundTrip, StringComparison.Ordinal);
        Assert.Contains("\"backpressure\": \"observed\"", roundTrip, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderRejectsUnsupportedStructuralSchemaVersion()
    {
        var json = QualityObservationJson.Serialize(CreateObservation())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCoreTaxonomyMajorIsFlaggedButNotDiscarded()
    {
        var future = CreateObservation() with { Taxonomy = new TaxonomyRef(QualityTaxonomyCatalogue.CoreId, "2.0.0", "sha256:" + new string('0', 64)) };
        var json = QualityObservationJson.Serialize(future);

        var loaded = QualityObservationJson.Deserialize(json);

        Assert.False(loaded.HasSupportedCoreTaxonomy);
        Assert.Equal("2.0.0", loaded.Taxonomy.Version);
        Assert.Equal(future.ObservationId, loaded.ObservationId);
    }

    [Fact]
    public void ExtensionTaxonomyIsNeverJudgedByTheCoreMajorCheck()
    {
        var extension = CreateObservation() with
        {
            Taxonomy = new TaxonomyRef("com.acme:resilience", "9.0.0", "sha256:" + new string('1', 64)),
        };

        Assert.True(extension.HasSupportedCoreTaxonomy);
    }

    [Theory]
    [InlineData("assessment")]
    [InlineData("evidenceStatus")]
    [InlineData("taxonomy")]
    [InlineData("producer")]
    public void FixtureMissingARequiredFieldFailsSchemaValidation(string requiredProperty)
    {
        var json = QualityObservationJson.Serialize(CreateObservation());
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals(requiredProperty)) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var mutated = JsonDocument.Parse(stream.ToArray());

        var evaluation = ObservationSchema.Value.Evaluate(mutated.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.False(evaluation.IsValid);
    }
}
