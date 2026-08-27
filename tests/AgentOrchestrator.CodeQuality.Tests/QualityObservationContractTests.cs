using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void DocumentValidatesAgainstItsSchema()
    {
        var json = QualityObservationJson.Serialize(CreateDocument());
        using var parsed = JsonDocument.Parse(json);

        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void SerializerOrdersSchemaFieldsFirst()
    {
        var json = QualityObservationJson.Serialize(CreateDocument());

        Assert.True(json.IndexOf("\"$schema\"", StringComparison.Ordinal) <
                    json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) <
                    json.IndexOf("\"observationId\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundTripPreservesExplicitExtensionData()
    {
        var extensions = new Dictionary<string, JsonElement>
        {
            ["com.acme:backpressure-window-ms"] = JsonDocument.Parse("500").RootElement.Clone(),
        };
        var original = CreateDocument() with { Extensions = extensions };

        var json = QualityObservationJson.Serialize(original);
        var loaded = QualityObservationJson.Deserialize(json);

        Assert.NotNull(loaded.Extensions);
        Assert.Equal(500, loaded.Extensions!["com.acme:backpressure-window-ms"].GetInt32());
        Assert.Equal(json, QualityObservationJson.Serialize(loaded));
    }

    [Fact]
    public void UnknownExtensionAspectRoundTripsAndIsExcludedFromCoreAggregation()
    {
        var extensionAspect = new ObservationAspectResult(
            "com.acme:resilience.backpressure", ObservationAssessment.Concern, "Extension catalogue judgement.");
        var original = CreateDocument();
        var withExtension = original with { Aspects = [.. original.Aspects, extensionAspect] };

        var loaded = QualityObservationJson.Deserialize(QualityObservationJson.Serialize(withExtension));

        Assert.Contains(loaded.Aspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
        var core = QualityTaxonomyCatalogueResolver.LoadCore();
        var coreAspects = loaded.Aspects.Where(aspect => core.IsKnownAspect(aspect.AspectId)).ToArray();
        Assert.DoesNotContain(coreAspects, aspect => aspect.AspectId == "com.acme:resilience.backpressure");
        Assert.Contains(coreAspects, aspect => aspect.AspectId == "code.correctness");
    }

    [Fact]
    public void ParseQuarantinesAnUnsupportedMajorWithoutDiscardingRawData()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var result = QualityObservationJson.Parse(json);

        Assert.False(result.Supported);
        Assert.Null(result.Document);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal("code.correctness", result.Raw.GetProperty("aspects")[0].GetProperty("aspectId").GetString());
    }

    [Fact]
    public void ParseAcceptsTheSupportedMajor()
    {
        var json = QualityObservationJson.Serialize(CreateDocument());

        var result = QualityObservationJson.Parse(json);

        Assert.True(result.Supported);
        Assert.NotNull(result.Document);
        Assert.Equal(1, result.SchemaVersion);
    }

    [Fact]
    public void DeserializeRejectsAFindingThatReferencesUnknownEvidence()
    {
        var document = CreateDocument();
        var badFinding = document.Findings[0] with { EvidenceRefs = ["missing-evidence"] };
        var withBadFinding = document with { Findings = [badFinding] };
        // Serialize() validates too, so bypass it here to produce syntactically valid JSON
        // that still carries the dangling evidence reference for Deserialize() to reject.
        var json = JsonSerializer.Serialize(withBadFinding, QualityObservationJson.Options);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("unknown evidence id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeRejectsAnUnsupportedSchemaVersion()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 3", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducerKindIsAlwaysExplicitAndNeverDefaultsToAgent()
    {
        var document = CreateDocument() with
        {
            Producer = new ObservationProducer(ProducerKind.Unknown),
        };

        var loaded = QualityObservationJson.Deserialize(QualityObservationJson.Serialize(document));

        Assert.Equal(ProducerKind.Unknown, loaded.Producer.Kind);
    }

    private static QualityObservationDocument CreateDocument()
    {
        var unitId = "qs-v1/dotnet/file/" + new string('a', 64);
        var manifestHash = "sha256:" + new string('b', 64);
        var promptHash = "sha256:" + new string('c', 64);
        var taxonomyDigest = "sha256:" + new string('d', 64);
        var observationId = "observation-sha256:" + new string('e', 64);
        var occurrenceFingerprint = "sha256:" + new string('f', 64);

        return new QualityObservationDocument
        {
            ObservationId = observationId,
            Taxonomy = new QualityTaxonomyReference(QualityTaxonomyCatalogue.CoreId, QualityTaxonomyCatalogue.CoreVersion, taxonomyDigest),
            Subject = new ObservationSubject(unitId, manifestHash),
            Profile = new ObservationProfile("file-code-review", "1.0.0", promptHash),
            Producer = new ObservationProducer(
                ProducerKind.Agent, "codex", "openai", "gpt-5.4-mini", "gpt-5.4-mini", "high", "2026-07-24", "quality-run-1", "review-run-1"),
            EvidenceStatus = EvidenceStatus.Available,
            Evidence =
            [
                new ObservationEvidence(
                    "ev-1", ObservationEvidenceKind.SourceCode,
                    new ObservationEvidenceLocator("src/A.cs", SymbolId: "M:A.Run"),
                    "Unchecked value reaches the dereference."),
            ],
            Aspects =
            [
                new ObservationAspectResult(
                    "code.correctness", ObservationAssessment.Fail, "One high-severity defect is evidenced.",
                    new ReviewGrade(68, GradeBand.D, "Null-deref defect drops the score below the D band floor.")),
            ],
            Assessment = ObservationAssessment.Fail,
            Findings =
            [
                new ObservationFinding(
                    "of-1", "code.correctness", FindingSeverity.High, ["ev-1"],
                    new ObservationFindingSource(ProducerKind.Agent, "self"),
                    OccurrenceFingerprint: occurrenceFingerprint,
                    FingerprintAlgorithm: "quality-studio-occurrence-v2",
                    RuleRef: "built-in/code.correctness.null-deref@1"),
            ],
        };
    }
}
