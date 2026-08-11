using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityFindingContractTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-finding.v1.schema.json"))));

    [Theory]
    [InlineData("quality-finding.source-located.v1.json", "standing-unit", 1)]
    [InlineData("quality-finding.task.v1.json", "task-change", 0)]
    public void Fixtures_validate_and_round_trip(string fixture, string subjectType, int locationCount)
    {
        var json = File.ReadAllText(Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "samples", fixture));
        using var parsed = JsonDocument.Parse(json);
        var evaluation = Schema.Value.Evaluate(parsed.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        Assert.True(evaluation.IsValid, evaluation.ToString());
        var finding = QualityFindingJson.Deserialize(json);
        var roundTrip = QualityFindingJson.Serialize(finding);
        using var roundTripJson = JsonDocument.Parse(roundTrip);
        var roundTripEvaluation = Schema.Value.Evaluate(roundTripJson.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(roundTripEvaluation.IsValid, roundTripEvaluation.ToString());
        Assert.Equal(subjectType, roundTripJson.RootElement.GetProperty("subject").GetProperty("type").GetString());
        Assert.Equal(locationCount, finding.Locations.Count);
        Assert.False(roundTripJson.RootElement.TryGetProperty("grade", out _));
        Assert.False(roundTripJson.RootElement.TryGetProperty("disposition", out _));
    }

    [Fact]
    public void Review_finding_converter_preserves_v2_vocabulary_and_source_location()
    {
        var source = new ReviewFinding(
            "missing-cancellation-token",
            "correctness",
            FindingSeverity.Medium,
            "Cancellation is not propagated",
            "The operation ignores cancellation.",
            "Pass the cancellation token.",
            [new FindingLocation("src/Example.cs", new FindingRange(
                new FindingPosition(12, 9), new FindingPosition(12, 31)))],
            "sha256:" + new string('c', 64),
            "async:cancellation",
            "Call site evidence");
        var subject = new StandingUnitFindingSubject(
            "quality-studio",
            "qs-v1/generic/file/" + new string('a', 64),
            "src/Example.cs",
            ReviewKind.Code,
            ManifestHash.Subject(new string('b', 64)));

        var converted = QualityFindingEnvelope.FromReviewFinding(
            source, subject, new QualityFindingProducer(QualityFindingProducerKind.Agent, "codex", "1.0.0", "run-1"));

        Assert.Equal(source.RuleId, converted.RuleId);
        Assert.Equal(source.Locations, converted.Locations);
        Assert.Equal(FindingIdentity.Canonicalization, converted.FingerprintCanonicalization);
        AssertValid(converted);
    }

    [Fact]
    public void Delta_converter_creates_a_valid_task_change_envelope()
    {
        var subject = new TaskChangeFindingSubject(
            "quality-studio",
            new string('a', 40),
            new string('b', 40),
            new string('b', 40),
            "sha256:" + new string('d', 64));
        var item = new FindingDeltaItem(
            "sha256:" + new string('e', 64),
            "unit-1",
            "src/Example.cs",
            "code",
            "async:cancellation",
            "high",
            "Cancellation is not propagated");

        var converted = QualityFindingEnvelope.FromFindingDeltaItem(item, subject);

        Assert.IsType<TaskChangeFindingSubject>(converted.Subject);
        Assert.Equal("src/Example.cs", Assert.Single(converted.Locations).Path);
        AssertValid(converted);
    }

    [Fact]
    public void Task_text_fingerprint_declares_and_applies_its_canonicalization()
    {
        var first = QualityFindingEnvelope.ComputeTaskTextFingerprint(
            "delivery:test", "Missing evidence", "No test\r\nwas supplied.", "Add  a test.");
        var second = QualityFindingEnvelope.ComputeTaskTextFingerprint(
            "delivery:test", "Missing evidence", "No test was supplied.", "Add a test.");

        Assert.Equal(first, second);
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal(71, first.Length);
    }

    private static void AssertValid(QualityFindingEnvelope finding)
    {
        using var json = JsonDocument.Parse(QualityFindingJson.Serialize(finding));
        var evaluation = Schema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
