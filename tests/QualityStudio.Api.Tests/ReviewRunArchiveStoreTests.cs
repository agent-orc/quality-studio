using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly Dictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    [Fact]
    public void Four_v1_contracts_validate_and_two_stopped_attempts_remain_readable()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("review-contract", new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero));
            store.CreateRun(run);
            store.AppendOperation(CreateOperation(run.RunId));
            store.AppendFinding(CreateFinding(run.RunId));
            store.CreateAttempt(CreateAttempt(run.RunId, 1, "capped", "partial"));
            store.CreateAttempt(CreateAttempt(run.RunId, 2, "done", "complete"));

            var loaded = store.Load(run.RunId);

            Assert.Equal(run.RunId, loaded.Run.RunId);
            Assert.Equal(run.CreatedAt, loaded.Run.CreatedAt);
            Assert.Equal(run.Targets, loaded.Run.Targets);
            Assert.Single(loaded.Operations);
            Assert.Single(loaded.Findings);
            Assert.Equal([1, 2], loaded.Attempts.Select(attempt => attempt.Attempt));
            Assert.Equal(["capped", "done"], loaded.Attempts.Select(attempt => attempt.Outcome));
            Assert.EndsWith(Path.Combine(".quality", "run-history", "2026-08", run.RunId),
                Path.GetDirectoryName(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "run.json")),
                StringComparison.Ordinal);

            Validate(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "run.json"),
                "run-record.v1.schema.json");
            ValidateJsonLine(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "operations.jsonl"),
                "run-operation.v1.schema.json");
            ValidateJsonLine(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "findings.jsonl"),
                "run-finding.v1.schema.json");
            Validate(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "attempts", "0001.json"),
                "run-attempt.v1.schema.json");
            Validate(Path.Combine(store.HistoryPath, "2026-08", run.RunId, "attempts", "0002.json"),
                "run-attempt.v1.schema.json");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Immutable_documents_reject_overwrite_and_identifiers_cannot_escape_repository()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-archive-confinement-").FullName;
        try
        {
            var store = new ReviewRunArchiveStore(root);
            var run = CreateRun("review-create-only", DateTimeOffset.UtcNow);
            store.CreateRun(run);
            store.CreateAttempt(CreateAttempt(run.RunId, 1, "capped", "partial"));

            Assert.Throws<IOException>(() => store.CreateRun(run));
            Assert.Throws<IOException>(() =>
                store.CreateAttempt(CreateAttempt(run.RunId, 1, "done", "complete")));
            Assert.Throws<ArgumentException>(() =>
                store.CreateRun(CreateRun("../outside", DateTimeOffset.UtcNow)));
            Assert.Throws<ArgumentException>(() => store.Load(".."));
            Assert.False(Directory.Exists(Path.Combine(root, "outside")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ReviewRunArchiveRecord CreateRun(string runId, DateTimeOffset createdAt) => new(
        ReviewRunArchiveSchemas.Run,
        ReviewRunArchiveSchemas.Version,
        runId,
        "default",
        createdAt,
        new ReviewRunArchiveSubject("project-sample", ".", "project"),
        "code",
        [new ReviewRunArchiveTarget(
            "file-sample", "Sample.cs", "Sample.cs",
            Hash('a'), "review-code", Hash('b'), Hash('c'))],
        new ReviewRunArchiveConfiguration(
            "claude-sonnet-5", "high", "test-agent", false, 1000, null,
            new ReviewRunEstimate(1, 2, 800, 200, 40, 0.01m, "USD", "priced", 3, "fixture")),
        new ReviewRunArchiveSourceRevision("0123456789abcdef", true));

    private static ReviewRunArchiveOperation CreateOperation(string runId) => new(
        ReviewRunArchiveSchemas.Operation,
        ReviewRunArchiveSchemas.Version,
        runId,
        "operation-0001",
        1,
        1,
        "file-sample",
        "Sample.cs",
        "file",
        "done",
        new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
        "provider-1",
        Hash('d'),
        Hash('e'),
        ".quality/reviews/Sample.cs.review-meta.code.json",
        new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
        null,
        new ReviewRunArchiveGrade(84, "B", "Fixture grade."),
        null,
        null);

    private static ReviewRunArchiveFinding CreateFinding(string runId) => new(
        ReviewRunArchiveSchemas.Finding,
        ReviewRunArchiveSchemas.Version,
        runId,
        "operation-0001",
        Hash('f'),
        "finding-1",
        "quality.test",
        "medium",
        "Archived fixture finding",
        [new ReviewRunArchiveFindingLocation("Sample.cs", 1, 1, 1, 10)],
        "open");

    private static ReviewRunArchiveAttempt CreateAttempt(
        string runId,
        int attempt,
        string outcome,
        string completeness)
    {
        var totals = new ReviewRunArchiveAttemptTotals(
            1, 1, 0, 0, new TokenUsage(200, 40, 10, 5, 1000), 0.01m, "USD", "priced");
        return new ReviewRunArchiveAttempt(
            ReviewRunArchiveSchemas.Attempt,
            ReviewRunArchiveSchemas.Version,
            runId,
            attempt,
            outcome,
            completeness,
            new DateTimeOffset(2026, 8, 11, 8, attempt, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, attempt + 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, attempt + 1, 1, TimeSpan.Zero),
            totals,
            totals,
            [],
            1000,
            null,
            new ReviewRunArchiveEstimateDeviation(0, 0, null),
            ["2026-08"],
            ["operation-0001"],
            "done",
            new ReviewRunArchiveQualitySummary(84, "B", null, 1, "medium"));
    }

    private static string Hash(char character) => "sha256:" + new string(character, 64);

    private static void Validate(string documentPath, string schemaFile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(documentPath));
        Validate(document.RootElement, schemaFile);
    }

    private static void ValidateJsonLine(string documentPath, string schemaFile)
    {
        using var document = JsonDocument.Parse(Assert.Single(File.ReadLines(documentPath)));
        Validate(document.RootElement, schemaFile);
    }

    private static void Validate(JsonElement document, string schemaFile)
    {
        JsonSchema schema;
        lock (Schemas)
        {
            if (!Schemas.TryGetValue(schemaFile, out schema!))
            {
                schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(), "schemas", schemaFile)));
                Schemas.Add(schemaFile, schema);
            }
        }
        var result = schema.Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "QualityStudio.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
