using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RunArchiveContractTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();
    private static readonly Dictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    public static TheoryData<string, string> Fixtures => new()
    {
        { "run-record.v1.schema.json", "run-record.v1.sample.json" },
        { "run-operation.v1.schema.json", "run-operation.v1.sample.json" },
        { "run-finding.v1.schema.json", "run-finding.v1.sample.json" },
        { "run-attempt.v1.schema.json", "run-attempt.v1.sample.json" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixtures_validate_against_their_schema(string schemaFile, string sampleFile)
    {
        using var parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "samples", sampleFile)));

        AssertValid(schemaFile, parsed.RootElement);
    }

    [Fact]
    public void Run_record_round_trips_through_its_typed_record_and_stays_valid()
    {
        var record = new RunRecord(
            "run-2026-08-27-0001",
            "quality-studio",
            new RunArchiveNode("qs-v1/dotnet/module/" + new string('a', 64), "src/QualityStudio.Api"),
            "module",
            "code",
            DateTimeOffset.Parse("2026-08-27T10:15:00Z"),
            [new RunArchiveTarget("src/QualityStudio.Api/ReviewJobs.cs", "sha256:" + new string('a', 64))],
            new RunArchiveConfiguration("claude-sonnet-5", "codex", "high", false),
            new RunArchiveSourceRevision(false, "3e0b6559faa700183a16e2ffda1a522fd75c9aa6"),
            Cap: new RunArchiveCap(TokenCap: 500_000));

        AssertValid("run-record.v1.schema.json", RunArchiveJson.Serialize(record));
    }

    [Fact]
    public void Run_operation_round_trips_through_its_typed_record_and_stays_valid()
    {
        var operation = new RunOperation(
            "run-2026-08-27-0001",
            "run-2026-08-27-0001:0001:abc123",
            0,
            1,
            "qs-v1/dotnet/file/" + new string('e', 64),
            "src/QualityStudio.Api/ReviewJobs.cs",
            "file",
            "done",
            DateTimeOffset.Parse("2026-08-27T10:15:42Z"),
            RunOperationVerdict.ForGrade(new GradeSnapshot(88, "B")));

        AssertValid("run-operation.v1.schema.json", RunArchiveJson.Serialize(operation));
    }

    [Fact]
    public void Run_finding_round_trips_through_its_typed_record_and_stays_valid()
    {
        var finding = new RunFinding(
            "run-2026-08-27-0001",
            "run-2026-08-27-0001:0001:abc123",
            "finding-missing-cancellation",
            "sha256:" + new string('e', 64),
            "quality.correctness.cancellation",
            "medium",
            "Cancellation is not propagated",
            [new FindingLocation("src/QualityStudio.Api/ReviewJobs.cs")],
            "open",
            DateTimeOffset.Parse("2026-08-27T10:15:42Z"));

        AssertValid("run-finding.v1.schema.json", RunArchiveJson.Serialize(finding));
    }

    [Fact]
    public void Run_attempt_round_trips_through_its_typed_record_and_stays_valid()
    {
        var tokens = new TokenUsage(6000, 2000, 0, 0, 15000);
        var attempt = new RunAttempt(
            "run-2026-08-27-0001",
            1,
            "capped",
            "partial",
            DateTimeOffset.Parse("2026-08-27T10:16:00Z"),
            new RunArchiveCounters(2, 1, 0, 1),
            new RunArchiveCounters(2, 1, 0, 1),
            new RunArchiveSpend(tokens, "priced", 0.61m, "USD"),
            new RunArchiveSpend(tokens, "priced", 0.61m, "USD"),
            [],
            new RunArchiveCap(TokenCap: 8000),
            ["2026-08"],
            new RunArchiveQualitySummary(1, new GradeSnapshot(88, "B"), null, "medium"),
            StopReason: "Token cap of 8,000 reached after 8,000 tokens.");

        AssertValid("run-attempt.v1.schema.json", RunArchiveJson.Serialize(attempt));
    }

    private static void AssertValid(string schemaFile, string json)
    {
        using var document = JsonDocument.Parse(json);
        AssertValid(schemaFile, document.RootElement);
    }

    private static void AssertValid(string schemaFile, JsonElement element)
    {
        var evaluation = LoadSchema(schemaFile).Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, JsonSerializer.Serialize(evaluation));
    }

    private static JsonSchema LoadSchema(string schemaFile)
    {
        lock (Schemas)
        {
            if (!Schemas.TryGetValue(schemaFile, out var schema))
            {
                schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot, "schemas", schemaFile)));
                Schemas[schemaFile] = schema;
            }
            return schema;
        }
    }
}
