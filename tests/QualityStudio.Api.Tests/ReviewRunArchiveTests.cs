using System.Collections.Concurrent;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 11, 9, 30, 0, TimeSpan.Zero);
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    [Fact]
    public void Archived_documents_validate_against_their_published_schemas()
    {
        using var directory = new ArchiveDirectory();
        var store = new ReviewRunArchiveStore(directory.Root);
        store.CreateRun(Run("review-schema"));
        store.AppendOperation(Operation("review-schema", "Sample.cs", 0, 1));
        store.AppendOperation(Operation("review-schema", ReviewOperationId.AggregateKey, 1, 1, level: "project"));
        store.AppendFinding(Finding("review-schema", "Sample.cs"));
        store.CreateAttempt(Attempt("review-schema", 1, "capped", "partial"));

        var runDirectory = store.RunDirectory("review-schema", CreatedAt);
        AssertValid("run-record.v1.schema.json", File.ReadAllText(Path.Combine(runDirectory, "run.json")));
        foreach (var line in File.ReadAllLines(Path.Combine(runDirectory, "operations.jsonl")))
            AssertValid("run-operation.v1.schema.json", line);
        foreach (var line in File.ReadAllLines(Path.Combine(runDirectory, "findings.jsonl")))
            AssertValid("run-finding.v1.schema.json", line);
        AssertValid("run-attempt.v1.schema.json",
            File.ReadAllText(Path.Combine(runDirectory, "attempts", "0001.json")));

        Assert.EndsWith(Path.Combine("run-history", "2026-08", "review-schema"), runDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_only_documents_reject_overwrite_and_stay_byte_identical()
    {
        using var directory = new ArchiveDirectory();
        var store = new ReviewRunArchiveStore(directory.Root);
        store.CreateRun(Run("review-create-only"));
        store.CreateAttempt(Attempt("review-create-only", 1, "capped", "partial"));
        var runPath = Path.Combine(store.RunDirectory("review-create-only", CreatedAt), "run.json");
        var attemptPath = Path.Combine(store.RunDirectory("review-create-only", CreatedAt), "attempts", "0001.json");
        var runBytes = File.ReadAllBytes(runPath);
        var attemptBytes = File.ReadAllBytes(attemptPath);

        var rewritten = Run("review-create-only") with { Kind = "security", Provenance = ReviewRunArchiveStore.ProvenanceMigrated };
        Assert.Throws<IOException>(() => store.CreateRun(rewritten));
        Assert.False(store.TryCreateRun(rewritten));
        Assert.Throws<IOException>(() => store.CreateAttempt(
            Attempt("review-create-only", 1, "done", "complete")));

        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(attemptBytes, File.ReadAllBytes(attemptPath));
        Assert.Equal("code", store.Load("review-create-only").Run.Kind);
    }

    [Fact]
    public void Capped_run_that_resumes_keeps_both_stopped_attempts_readable()
    {
        using var directory = new ArchiveDirectory();
        var writer = new ReviewRunArchiveStore(directory.Root);
        writer.CreateRun(Run("review-capped"));
        writer.AppendOperation(Operation("review-capped", "Sample.cs", 0, 1));
        writer.AppendFinding(Finding("review-capped", "Sample.cs"));
        Assert.Equal(1, writer.NextAttemptNumber("review-capped"));
        writer.CreateAttempt(Attempt("review-capped", 1, "capped", "partial"));
        var firstAttemptBytes = File.ReadAllBytes(Path.Combine(
            writer.RunDirectory("review-capped", CreatedAt), "attempts", "0001.json"));

        // The resume is a second attempt of the same logical run: completed work is not repeated.
        Assert.Equal(2, writer.NextAttemptNumber("review-capped"));
        writer.AppendOperation(Operation("review-capped", "Second.cs", 1, 2));
        writer.AppendOperation(Operation("review-capped", ReviewOperationId.AggregateKey, 2, 2, level: "project"));
        writer.CreateAttempt(Attempt("review-capped", 2, "done", "complete"));

        // A fresh reader stands in for a restarted process or a clean clone of the tracked files.
        var archive = new ReviewRunArchiveStore(directory.Root).Load("review-capped");
        Assert.Equal(2, archive.Attempts.Count);
        Assert.Equal("capped", archive.Attempts[0].Outcome);
        Assert.Equal("partial", archive.Attempts[0].Completeness);
        Assert.Equal(2, archive.LatestAttempt!.Attempt);
        Assert.Equal("done", archive.LatestAttempt.Outcome);
        Assert.Equal(firstAttemptBytes, File.ReadAllBytes(Path.Combine(
            writer.RunDirectory("review-capped", CreatedAt), "attempts", "0001.json")));

        Assert.Equal(3, archive.Operations.Count);
        Assert.Equal([1, 2, 2], archive.Operations.Select(operation => operation.Attempt));
        Assert.Equal([0, 1, 2], archive.Operations.Select(operation => operation.Ordinal));
        Assert.Equal(archive.Operations.Select(operation => operation.OperationId).Distinct().Count(),
            archive.Operations.Count);
        Assert.Equal("Sample.cs", Assert.Single(archive.Findings).Locations[0].Path);
        Assert.Equal(archive.Operations[0].OperationId, archive.Findings[0].OperationId);
    }

    [Fact]
    public void Archive_paths_stay_inside_the_repository_run_history_root()
    {
        using var directory = new ArchiveDirectory();
        var store = new ReviewRunArchiveStore(directory.Root);

        Assert.Throws<ArgumentException>(() => store.RunDirectory("../escape", CreatedAt));
        Assert.Throws<ArgumentException>(() => store.RunDirectory("nested/run", CreatedAt));
        Assert.Throws<ArgumentException>(() => store.CreateRun(Run("..")));
        Assert.Throws<ArgumentException>(() => store.CreateRun(Run("run id with spaces")));

        var expectedRoot = Path.Combine(Path.GetFullPath(directory.Root), ".quality", "run-history");
        Assert.Equal(expectedRoot, store.ArchivePath);
        store.CreateRun(Run("review-confined"));
        Assert.StartsWith(expectedRoot + Path.DirectorySeparatorChar,
            store.RunDirectory("review-confined", CreatedAt), StringComparison.Ordinal);
        Assert.False(store.Exists("review-missing"));
        Assert.False(store.TryLoad("review-missing", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Corrupt_archive_is_surfaced_as_typed_history_corrupt_and_never_silently_omitted()
    {
        using var directory = new ArchiveDirectory();
        var store = new ReviewRunArchiveStore(directory.Root);
        store.CreateRun(Run("review-readable"));
        store.CreateAttempt(Attempt("review-readable", 1, "done", "complete"));
        store.CreateRun(Run("review-broken"));
        store.AppendOperation(Operation("review-broken", "Sample.cs", 0, 1));
        File.AppendAllText(Path.Combine(store.RunDirectory("review-broken", CreatedAt), "operations.jsonl"),
            "{\"runId\":\n");

        var failure = Assert.Throws<ReviewRunArchiveException>(() => store.Load("review-broken"));
        Assert.Equal(ReviewRunArchiveException.HistoryCorrupt, failure.Code);
        Assert.Contains("line 2", failure.Message, StringComparison.Ordinal);

        var reported = new List<string>();
        var loaded = store.LoadAll((path, exception) => reported.Add($"{Path.GetFileName(path)}:{exception.Code}"));
        Assert.Equal("review-readable", Assert.Single(loaded).Run.RunId);
        Assert.Equal($"review-broken:{ReviewRunArchiveException.HistoryCorrupt}", Assert.Single(reported));

        Assert.Throws<ReviewRunArchiveException>(() => store.Load("review-absent"));
    }

    [Fact]
    public void Operation_identity_is_derived_and_stable_across_recovery()
    {
        var first = ReviewOperationId.For("review-1", "Sample.cs");

        Assert.Equal(first, ReviewOperationId.For("review-1", "Sample.cs"));
        Assert.NotEqual(first, ReviewOperationId.For("review-2", "Sample.cs"));
        Assert.NotEqual(first, ReviewOperationId.For("review-1", "Second.cs"));
        Assert.NotEqual(first, ReviewOperationId.For("review-1", ReviewOperationId.AggregateKey));
        Assert.Matches("^op-[a-f0-9]{24}$", first);
        Assert.Equal("@aggregate", ReviewOperationId.AggregateKey);
    }

    private static ReviewRunArchiveRecord Run(string runId) => new(
        ReviewRunArchiveJson.RunSchemaId,
        1,
        runId,
        RepositoryRegistry.DefaultRepositoryId,
        "code",
        CreatedAt,
        CreatedAt.AddMinutes(4),
        new ReviewRunArchiveSubject("project-sample", "Sample", ".", "project"),
        [
            new ReviewRunArchiveTarget("file-sample", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64)),
            new ReviewRunArchiveTarget("file-second", "Second.cs", "Second.cs", "sha256:" + new string('b', 64)),
        ],
        "sha256:" + new string('c', 64),
        new ReviewRunArchiveConfiguration("claude-sonnet-5", "high", "test-agent", false, false),
        new ReviewRunArchiveCap(5, null),
        new ReviewRunArchiveEstimate(2, 3, 1200, 240, 0.02m, "USD", "priced", 4, "history"),
        new ReviewRunArchiveRevision(new string('d', 40), true),
        ReviewRunArchiveStore.ProvenanceLive);

    private static ReviewRunOperationRecord Operation(
        string runId, string operationKey, int ordinal, int attempt, string level = "file") => new(
        ReviewRunArchiveJson.OperationSchemaId,
        1,
        runId,
        ReviewOperationId.For(runId, operationKey),
        ordinal,
        attempt,
        level == "file" ? "file-sample" : "project-sample",
        level == "file" ? operationKey : ".",
        level,
        "done",
        CreatedAt,
        CreatedAt.AddMinutes(1),
        "provider-run-7",
        "sha256:" + new string('e', 64),
        "sha256:" + new string('f', 64),
        ".quality/reviews/sample.meta.json",
        "sha256:" + new string('0', 64),
        new ReviewRunArchiveVerdict("grade", "B", 84, "B"),
        null);

    private static ReviewRunFindingRecord Finding(string runId, string path) => new(
        ReviewRunArchiveJson.FindingSchemaId,
        1,
        runId,
        ReviewOperationId.For(runId, path),
        "fingerprint-1",
        "finding-1",
        "quality.rule.1",
        "medium",
        "Unbounded retry loop",
        [new QualityFindingLocation(path, 12, 5, 18, 9)],
        "open",
        CreatedAt.AddMinutes(1));

    private static ReviewRunAttemptRecord Attempt(
        string runId, int attempt, string outcome, string completeness) => new(
        ReviewRunArchiveJson.AttemptSchemaId,
        1,
        runId,
        attempt,
        outcome,
        completeness,
        CreatedAt,
        CreatedAt.AddMinutes(3),
        CreatedAt.AddMinutes(4),
        new ReviewRunArchiveCounters(1, 0, 0, 1, 0),
        new ReviewRunArchiveSpend(1, 900, 120, 0, null, 4200, 0.01m, "USD", "priced"),
        [],
        outcome == "capped" ? "Token cap of 5 reached after 1,020 tokens." : null,
        new ReviewRunArchiveCap(5, null),
        new ReviewRunArchiveDeviation(-25.0m, -50.0m, null),
        ["2026-08"],
        [ReviewOperationId.For(runId, "Sample.cs")],
        new ReviewRunArchiveQuality(84, "B", null, 1, "medium"));

    private static void AssertValid(string schemaFile, string json)
    {
        // Schemas are cached because the library registers each $id globally exactly once.
        var schema = Schemas.GetOrAdd(schemaFile, file =>
            JsonSchema.FromText(File.ReadAllText(Path.Combine(RepositoryRoot(), "schemas", file))));
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, $"{schemaFile}: {result}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The repository root with QualityStudio.slnx was not found.");
    }

    private sealed class ArchiveDirectory : IDisposable
    {
        public ArchiveDirectory() =>
            Root = Directory.CreateTempSubdirectory("quality-studio-run-archive-").FullName;

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
