using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityObservationContractTests
{
    private static readonly Lazy<JsonSchema> ObservationSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-observation.v1.schema.json"))));

    [Fact]
    public void SerializedDocument_validates_against_the_schema_and_round_trips()
    {
        var original = CreateDocument();

        var json = QualityObservationJson.Serialize(original);
        AssertValidatesAgainstSchema(json);
        var loaded = QualityObservationJson.Deserialize(json);

        Assert.Equal(original.ObservationId, loaded.ObservationId);
        Assert.Equal(original.Taxonomy, loaded.Taxonomy);
        Assert.Equal(original.Subject, loaded.Subject);
        Assert.Equal(original.Producer, loaded.Producer);
        Assert.Equal(original.Assessment, loaded.Assessment);
        Assert.Single(loaded.Findings);
        Assert.Equal(original.Findings[0].OccurrenceFingerprint, loaded.Findings[0].OccurrenceFingerprint);
        Assert.Equal(json, QualityObservationJson.Serialize(loaded));
    }

    [Fact]
    public void LoaderRejectsUnsupportedSchemaVersion()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var exception = Assert.Throws<JsonException>(() => QualityObservationJson.Deserialize(json));

        Assert.Contains("Unsupported quality observation schemaVersion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedTaxonomyMajor_isQuarantinedWithRawJsonPreserved()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"version\": \"1.0.0\",\n    \"digest\"", "\"version\": \"2.0.0\",\n    \"digest\"", StringComparison.Ordinal);

        var exception = Assert.Throws<UnsupportedTaxonomyMajorException>(() => QualityObservationJson.Deserialize(json));

        Assert.Equal("2.0.0", exception.TaxonomyVersion);
        Assert.Equal(json, exception.RawJson);
    }

    [Fact]
    public void Extensions_and_legacy_root_keys_survive_a_round_trip_without_becoming_core_data()
    {
        var original = CreateDocument() with
        {
            Extensions = ParseLocator("""{"com.acme:resilience.retryBudget": {"attempts": 3}}"""),
        };
        var json = QualityObservationJson.Serialize(original);
        var withLegacyRootKey = json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"x-future-field\": { \"enabled\": true },",
            StringComparison.Ordinal);

        AssertValidatesAgainstSchema(withLegacyRootKey);
        var loaded = QualityObservationJson.Deserialize(withLegacyRootKey);
        var roundTripped = QualityObservationJson.Serialize(loaded);

        Assert.Contains("com.acme:resilience.retryBudget", roundTripped, StringComparison.Ordinal);
        Assert.Contains("x-future-field", roundTripped, StringComparison.Ordinal);
        Assert.Single(loaded.Aspects);
        Assert.Equal("code.correctness", loaded.Aspects[0].AspectId);
    }

    [Fact]
    public void Schema_rejects_an_unrecognized_non_extension_top_level_field()
    {
        var json = QualityObservationJson.Serialize(CreateDocument())
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"unknownField\": true,", StringComparison.Ordinal);

        using var parsed = JsonDocument.Parse(json);
        var evaluation = ObservationSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(evaluation.IsValid);
    }

    private static void AssertValidatesAgainstSchema(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var evaluation = ObservationSchema.Value.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseLocator(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static QualityObservationDocument CreateDocument() => new()
    {
        ObservationId = "observation-sha256:" + new string('a', 64),
        Taxonomy = new TaxonomyReference("quality-studio/core", "1.0.0", "sha256:" + new string('b', 64)),
        Subject = new ObservationSubject("qs-v1/dotnet/file/" + new string('c', 64), "sha256:" + new string('d', 64)),
        Profile = new ObservationProfile("file-code-review", "1.0.0", "sha256:" + new string('e', 64), "sha256:" + new string('f', 64)),
        Producer = new ObservationProducer(
            QualityProducerKind.Agent, "codex", "openai", "gpt-5.4-mini", "gpt-5.4-mini", "high", "2026-07-24", "run-1", "review-1"),
        EvidenceStatus = QualityEvidenceStatus.Available,
        Evidence =
        [
            new ObservationEvidenceItem(
                "ev-1", QualityEvidenceKind.SourceCode,
                ParseLocator("""{"path": "src/A.cs", "symbolId": "M:A.Run"}"""),
                "Unchecked value reaches the dereference.", "sha256:" + new string('1', 64)),
        ],
        Aspects =
        [
            new ObservationAspect("code.correctness", QualityAssessment.Fail,
                "One high-severity defect is evidenced.", new ObservationGrade(68, GradeBand.D)),
        ],
        Assessment = QualityAssessment.Fail,
        Findings =
        [
            new ObservationFinding(
                "of-1", "sha256:" + new string('2', 64), "quality-studio-occurrence-v2",
                "code.correctness", FindingSeverity.High, ["ev-1"],
                new ObservationFindingSource(QualityProducerKind.Agent, "self"),
                IssueId: "issue-1", RuleRef: "built-in/code.correctness.null-deref@1"),
        ],
    };
}
