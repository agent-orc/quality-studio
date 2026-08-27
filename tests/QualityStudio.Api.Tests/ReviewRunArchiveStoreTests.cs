using System.Reflection;
using System.Text.Json;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    [Theory]
    [InlineData("run-record.v1")]
    [InlineData("run-operation.v1")]
    [InlineData("run-finding.v1")]
    [InlineData("run-attempt.v1")]
    public void Archive_contract_fixture_validates(string contract)
    {
        var root = FindRepositoryRoot();
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(root, "schemas", contract + ".schema.json")));
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "samples", contract + ".json")));

        var result = schema.Evaluate(fixture.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Store_preserves_two_create_only_attempts_for_one_capped_and_resumed_run()
    {
        var root = Directory.CreateTempSubdirectory("review-run-archive-");
        try
        {
            var store = new ReviewRunArchiveStore(root.FullName);
            var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
            var run = CreateRun(createdAt);
            store.CreateRun(run);
            store.AppendOperation(createdAt, CreateOperation(attempt: 1));
            store.AppendFinding(createdAt, CreateFinding());
            store.CreateAttempt(createdAt, CreateAttempt(1, "capped", "partial", createdAt.AddMinutes(2)));
            store.AppendOperation(createdAt, CreateOperation(attempt: 2) with
            {
                OperationId = "operation-sample-2",
                Ordinal = 2,
                FinishedAt = createdAt.AddMinutes(4),
            });
            store.CreateAttempt(createdAt, CreateAttempt(2, "done", "complete", createdAt.AddMinutes(4)) with
            {
                OperationIds = ["operation-sample-1", "operation-sample-2"],
            });

            var loaded = store.Load(createdAt, run.RunId);

            Assert.Equal([1, 2], loaded.Attempts.Select(item => item.Attempt));
            Assert.Equal(["capped", "done"], loaded.Attempts.Select(item => item.Outcome));
            Assert.Equal(2, loaded.Operations.Count);
            Assert.Single(loaded.Findings);
            Assert.Equal(Path.Combine(root.FullName, ".quality", "run-history", "2026-08", run.RunId),
                store.RunPath(createdAt, run.RunId));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Create_only_run_and_attempt_files_reject_overwrite()
    {
        var root = Directory.CreateTempSubdirectory("review-run-create-only-");
        try
        {
            var store = new ReviewRunArchiveStore(root.FullName);
            var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);
            var run = CreateRun(createdAt);
            var attempt = CreateAttempt(1, "capped", "partial", createdAt.AddMinutes(2));
            store.CreateRun(run);
            store.CreateAttempt(createdAt, attempt);

            Assert.Throws<IOException>(() => store.CreateRun(run));
            Assert.Throws<IOException>(() => store.CreateAttempt(createdAt, attempt));
            Assert.Equal("capped", Assert.Single(store.Load(createdAt, run.RunId).Attempts).Outcome);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Store_rejects_identifiers_and_repository_paths_that_escape_the_repository()
    {
        var root = Directory.CreateTempSubdirectory("review-run-confined-");
        try
        {
            var store = new ReviewRunArchiveStore(root.FullName);
            var createdAt = new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun(createdAt) with { RunId = "../escape" }));
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun(createdAt) with
            {
                Subject = new ReviewRunArchiveSubject("project", "../outside", "project"),
            }));
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun(createdAt) with
            {
                Targets = [CreateRun(createdAt).Targets[0] with { Path = Path.GetFullPath("/tmp/outside.cs") }],
            }));
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun(createdAt) with
            {
                Subject = new ReviewRunArchiveSubject("project", "C:\\outside", "project"),
            }));
            Assert.Throws<ArgumentException>(() => store.CreateRun(CreateRun(createdAt) with
            {
                RunId = "review id with spaces",
            }));
            Assert.False(File.Exists(Path.Combine(root.Parent!.FullName, "escape", "run.json")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static ReviewRunArchiveRun CreateRun(DateTimeOffset createdAt) => ReviewRunArchiveRun.Create(
        "review-sample-1",
        "quality-studio",
        createdAt,
        new ReviewRunArchiveSubject("project-quality-studio", ".", "project"),
        "code",
        [new ReviewRunArchiveTarget(
            "operation-sample-1", 1, "file-review-run-store", "ReviewRunStore.cs",
            "src/QualityStudio.Api/ReviewRunStore.cs",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
        new ReviewRunArchiveConfiguration("codex", "gpt-5.4", "high", false, ["code-review.v1"]),
        new ReviewRunArchiveCap(10_000, null),
        sourceRevision: new ReviewRunArchiveRevision(new string('a', 40), true));

    private static ReviewRunArchiveOperation CreateOperation(int attempt) => ReviewRunArchiveOperation.Create(
        "review-sample-1",
        "operation-sample-1",
        1,
        attempt,
        "file-review-run-store",
        "src/QualityStudio.Api/ReviewRunStore.cs",
        "file",
        "done",
        new DateTimeOffset(2026, 8, 11, 9, 0, 1, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 11, 9, 2, 0, TimeSpan.Zero),
        reviewedHash: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        resultSidecar: "src/QualityStudio.Api/ReviewRunStore.cs.review-meta.json",
        grade: new ReviewRunArchiveGrade(91, "A", "Meets the quality contract."));

    private static ReviewRunArchiveFinding CreateFinding() => ReviewRunArchiveFinding.Create(
        "review-sample-1",
        "operation-sample-1",
        "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        "archive-write-order",
        "durability:write-order",
        "high",
        "Archive row precedes durable sidecar",
        [new ReviewRunArchiveLocation("src/QualityStudio.Api/ReviewRunStore.cs", 120, 9, 120, 42)],
        "open");

    private static ReviewRunArchiveAttempt CreateAttempt(
        int attempt,
        string outcome,
        string completeness,
        DateTimeOffset finishedAt) => ReviewRunArchiveAttempt.Create(
        "review-sample-1",
        attempt,
        outcome,
        completeness,
        new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero),
        finishedAt,
        finishedAt.AddSeconds(1),
        new ReviewRunArchiveCounters(2, 1, 0, 1, 0, 1),
        new ReviewRunArchiveCounters(2, attempt, 0, Math.Max(0, 2 - attempt), 0, attempt),
        new ReviewRunArchiveSpend(8200, 1400, 2000, 300, 120000, 0.42m, "USD", "priced"),
        outcome == "capped" ? ["token-cap-reached"] : [],
        new ReviewRunArchiveCap(10_000, null),
        new ReviewRunArchiveEstimateDeviation(-4.2m, 2.1m, null),
        [".quality/usage/2026-08.jsonl"],
        ["operation-sample-1"],
        new ReviewRunArchiveQualitySummary(91, "A", null, 1, "high"));

    private static string FindRepositoryRoot()
    {
        var location = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        for (var current = new DirectoryInfo(location); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "QualityStudio.slnx"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Quality Studio repository root.");
    }
}
