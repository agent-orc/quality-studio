using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, JsonSchema> SchemaCache = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("run-record.v1.json", "run-record.v1.schema.json")]
    [InlineData("run-operation.v1.json", "run-operation.v1.schema.json")]
    [InlineData("run-operation.v1.security.json", "run-operation.v1.schema.json")]
    [InlineData("run-finding.v1.json", "run-finding.v1.schema.json")]
    [InlineData("run-attempt.v1.capped.json", "run-attempt.v1.schema.json")]
    [InlineData("run-attempt.v1.done.json", "run-attempt.v1.schema.json")]
    public void Fixtures_validate_against_their_schema(string fixture, string schemaFile)
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var schema = LoadSchema(root, schemaFile);
        var json = File.ReadAllText(Path.Combine(root, "samples", fixture));
        using var parsed = JsonDocument.Parse(json);
        AssertValid(schema, parsed.RootElement);
    }

    [Fact]
    public void Typed_run_record_round_trips_and_validates()
    {
        var schema = LoadSchema(RepositoryTestContext.FindRepositoryRoot(), "run-record.v1.schema.json");
        var target = new RunArchiveTarget(
            "qs-v1/generic/file/" + new string('a', 64), "Example.cs", "src/Example.cs", "sha256:" + new string('b', 64));
        var run = new RunRecord(
            ReviewRunArchiveStore.RunRecordSchemaUrl, 1, "run-1", "quality-studio", DateTimeOffset.UtcNow,
            "code", "file", target.UnitId, "src/Example.cs", [target],
            new RunArchiveConfiguration("runner-default", "model-default", "codex", false, null, null, false),
            new RunArchiveCap(null, null), null, null);

        AssertRoundTripValid(schema, run);
    }

    [Fact]
    public void Typed_run_operation_round_trips_and_validates()
    {
        var schema = LoadSchema(RepositoryTestContext.FindRepositoryRoot(), "run-operation.v1.schema.json");
        var operation = new RunOperationRecord(
            ReviewRunArchiveStore.RunOperationSchemaUrl, 1, "run-1", 1, ReviewRunArchiveStore.OperationId("run-1", "src/Example.cs"),
            0, "qs-v1/generic/file/" + new string('a', 64), "file", "src/Example.cs", "done",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "provider-run-1", "sha256:" + new string('b', 64),
            "sha256:" + new string('c', 64), "src/Example.cs.review-meta.json",
            new RunOperationVerdict("grade", 92, "A", null, null));

        AssertRoundTripValid(schema, operation);
    }

    [Fact]
    public void Typed_run_finding_round_trips_and_validates()
    {
        var schema = LoadSchema(RepositoryTestContext.FindRepositoryRoot(), "run-finding.v1.schema.json");
        var finding = new RunFindingRecord(
            ReviewRunArchiveStore.RunFindingSchemaUrl, 1, "run-1", 1,
            ReviewRunArchiveStore.OperationId("run-1", "src/Example.cs"), "sha256:" + new string('d', 64),
            "missing-cancellation-token", "async:cancellation", "medium", "Cancellation is not propagated", "open",
            [new RunFindingLocation("src/Example.cs", 12, 9, 12, 31)], DateTimeOffset.UtcNow);

        AssertRoundTripValid(schema, finding);
    }

    [Fact]
    public void Typed_run_attempt_round_trips_and_validates()
    {
        var schema = LoadSchema(RepositoryTestContext.FindRepositoryRoot(), "run-attempt.v1.schema.json");
        var attempt = new RunAttemptRecord(
            ReviewRunArchiveStore.RunAttemptSchemaUrl, 1, "run-1", 1, "capped", "partial",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new RunAttemptCounters(
                new RunAttemptFileCounts(4, 2, 0, 2, 0),
                new RunAttemptFileCounts(4, 2, 0, 2, 0)),
            new RunAttemptUsage(2, 4000, 1200, 0, 0, 9000, 0.42m, "USD", "priced"),
            new RunAttemptCap(5000, null, "reached", "Token cap of 5,000 reached after 5,200 tokens."),
            null, [], new RunAttemptLedger("run-1", ["2026-08"], ["op-a", "op-b"]),
            new RunAttemptQualitySummary(new RunAttemptGrade(78, "C"), null, new Dictionary<string, int> { ["medium"] = 1 }, "medium"));

        AssertRoundTripValid(schema, attempt);
    }

    private static JsonSchema LoadSchema(string root, string schemaFile) => SchemaCache.GetOrAdd(schemaFile,
        file => JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "schemas", file))));

    private static void AssertRoundTripValid<T>(JsonSchema schema, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        using var parsed = JsonDocument.Parse(json);
        AssertValid(schema, parsed.RootElement);

        var roundTripped = JsonSerializer.Deserialize<T>(json, JsonOptions);
        var roundTrippedJson = JsonSerializer.Serialize(roundTripped, JsonOptions);
        Assert.Equal(json, roundTrippedJson);
    }

    private static void AssertValid(JsonSchema schema, JsonElement element)
    {
        var evaluation = schema.Evaluate(element, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, evaluation.ToString());
    }
}
