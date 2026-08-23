using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Schema fixture validation for the RP-1 run-history contracts (run-record, run-operation,
/// run-finding, run-attempt v1) described in docs/operations/run-persistence/index.html#contract.</summary>
public sealed class ReviewRunArchiveContractTests
{
    private static readonly Lazy<JsonSchema> RunRecordSchema = SchemaFor("run-record.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunOperationSchema = SchemaFor("run-operation.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunFindingSchema = SchemaFor("run-finding.v1.schema.json");
    private static readonly Lazy<JsonSchema> RunAttemptSchema = SchemaFor("run-attempt.v1.schema.json");

    [Fact]
    public void Run_record_fixture_validates_and_round_trips()
    {
        var run = ReviewRunArchiveJson.DeserializeRun(ReadSample("run-record.v1.json"));
        AssertValid(RunRecordSchema, ReviewRunArchiveJson.SerializeRun(run));
        Assert.Equal("run-20260811-0001", run.RunId);
        Assert.Equal(2, run.Targets.Count);
        Assert.Equal("3e0b6559faa700183a16e2ffda1a522fd75c9aa6", run.SourceRevision?.CommitSha);
    }

    [Theory]
    [InlineData("run-operation.grade.v1.json")]
    [InlineData("run-operation.security.v1.json")]
    public void Run_operation_fixtures_validate_and_round_trip(string fixture)
    {
        var operation = ReviewRunArchiveJson.DeserializeOperation(ReadSample(fixture));
        AssertValid(RunOperationSchema, ReviewRunArchiveJson.SerializeOperation(operation));
    }

    [Fact]
    public void Run_operation_verdict_discriminates_grade_and_security()
    {
        var grade = ReviewRunArchiveJson.DeserializeOperation(ReadSample("run-operation.grade.v1.json"));
        var security = ReviewRunArchiveJson.DeserializeOperation(ReadSample("run-operation.security.v1.json"));

        Assert.IsType<GradeOperationVerdict>(grade.Verdict);
        Assert.Equal(82, ((GradeOperationVerdict)grade.Verdict!).Score);
        Assert.IsType<SecurityOperationVerdict>(security.Verdict);
        Assert.Equal(SecurityVerdict.Warn, ((SecurityOperationVerdict)security.Verdict!).Verdict);
    }

    [Fact]
    public void Run_finding_fixture_validates_and_round_trips()
    {
        var finding = ReviewRunArchiveJson.DeserializeFinding(ReadSample("run-finding.v1.json"));
        AssertValid(RunFindingSchema, ReviewRunArchiveJson.SerializeFinding(finding));
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("open", finding.State);
    }

    [Fact]
    public void Run_attempt_fixture_validates_and_round_trips()
    {
        var attempt = ReviewRunArchiveJson.DeserializeAttempt(ReadSample("run-attempt.v1.json"));
        AssertValid(RunAttemptSchema, ReviewRunArchiveJson.SerializeAttempt(attempt));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("capped", attempt.Outcome);
        Assert.Equal(SecurityVerdict.Warn, attempt.QualitySummary?.WorstSecurityVerdict);
    }

    [Fact]
    public void Serialize_rejects_a_document_stamped_with_the_wrong_schema()
    {
        var wrong = new RunRecord
        {
            Schema = "https://quality.studio/schemas/run-record.v2.schema.json",
            RunId = "run-1",
            RepositoryId = "repo",
            CreatedAt = DateTimeOffset.UtcNow,
            Subject = new RunRecordSubject("n", "n", ".", "project"),
            Kind = "code",
            Targets = [],
            Configuration = new RunRecordConfiguration("test-agent", false),
        };

        Assert.Throws<System.Text.Json.JsonException>(() => ReviewRunArchiveJson.SerializeRun(wrong));
    }

    private static void AssertValid(Lazy<JsonSchema> schema, string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var evaluation = schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    private static Lazy<JsonSchema> SchemaFor(string fileName) => new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryPaths.FindRepositoryRoot(), "schemas", fileName))));

    private static string ReadSample(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryPaths.FindRepositoryRoot(), "samples", fileName));
}
