using AgentOrchestrator.CodeQuality;
using System.Text.Json;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-run-archive-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Operation_ids_are_stable_normalized_and_validate_their_inputs()
    {
        var windowsPath = ReviewOperationId.Create("run-1", "src\\Subject.cs");
        var unixPath = ReviewOperationId.Create("run-1", "src/Subject.cs");

        Assert.Equal(unixPath, windowsPath);
        Assert.StartsWith("op-", unixPath, StringComparison.Ordinal);
        Assert.Equal(35, unixPath.Length);
        Assert.NotEqual(unixPath, ReviewOperationId.Create("run-2", "src/Subject.cs"));
        Assert.Throws<ArgumentException>(() => ReviewOperationId.Create("", "src/Subject.cs"));
        Assert.Throws<ArgumentException>(() => ReviewOperationId.Create("run-1", " "));
    }

    [Fact]
    public void Archive_round_trips_run_operations_findings_and_ordered_attempts()
    {
        Directory.CreateDirectory(repositoryRoot);
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var createdAt = new DateTimeOffset(2026, 8, 12, 22, 30, 0, TimeSpan.Zero);
        var run = CreateRun("run-complete", createdAt);

        store.CreateRun(run);

        Assert.EndsWith(ReviewRunArchiveStore.RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar),
            store.ArchiveRoot, StringComparison.Ordinal);
        Assert.True(store.TryFindRun(run.RunId, out var runDirectory));
        AssertRunEquivalent(run, store.LoadRun(run.RunId));
        Assert.Empty(store.LoadAttempts(run.RunId));
        Assert.Empty(store.LoadOperations(run.RunId));
        Assert.Empty(store.LoadFindings(run.RunId));

        var operation = new RunOperationRecord
        {
            RunId = run.RunId,
            OperationId = ReviewOperationId.Create(run.RunId, "src/Subject.cs"),
            Attempt = 1,
            Ordinal = 2,
            Path = "src/Subject.cs",
            Level = "file",
            State = "done",
            StartedAt = createdAt.AddSeconds(1),
            FinishedAt = createdAt.AddSeconds(2),
            ProviderRunId = "provider-1",
            ReviewedHash = "sha256:" + new string('a', 64),
            SidecarPath = ".quality/reviews/subject.json",
            Verdict = "pass",
            GradeScore = 92,
            GradeBand = "A",
        };
        var finding = new RunFindingRecord
        {
            RunId = run.RunId,
            OperationId = operation.OperationId,
            Fingerprint = "sha256:" + new string('b', 64),
            FindingId = "finding-1",
            RuleId = "quality.archive",
            Severity = "medium",
            Title = "Archived finding",
            Locations = [new FindingLocation("src/Subject.cs")],
            StateAtObservation = "open",
        };
        var laterAttempt = CreateAttempt(run.RunId, 2, createdAt.AddMinutes(2), "done", "complete");
        var firstAttempt = CreateAttempt(run.RunId, 1, createdAt.AddMinutes(1), "capped", "partial");

        store.AppendOperation(operation);
        store.AppendOperation(operation with { Ordinal = 3, State = "skipped", Verdict = null });
        store.AppendFinding(finding);
        store.WriteAttempt(laterAttempt);
        store.WriteAttempt(firstAttempt);
        File.AppendAllText(Path.Combine(runDirectory, "operations.jsonl"), Environment.NewLine);

        var operations = store.LoadOperations(run.RunId);
        Assert.Equal(2, operations.Count);
        Assert.Equal(operation, operations[0]);
        Assert.Equal("skipped", operations[1].State);
        var loadedFinding = Assert.Single(store.LoadFindings(run.RunId));
        Assert.Equal(finding with { Locations = loadedFinding.Locations }, loadedFinding);
        Assert.Equal(finding.Locations.ToArray(), loadedFinding.Locations.ToArray());
        Assert.Equal([1, 2], store.LoadAttempts(run.RunId).Select(attempt => attempt.Attempt));
        Assert.Equal("capped", store.LoadAttempts(run.RunId)[0].Outcome);
        Assert.Equal("done", store.LoadAttempts(run.RunId)[1].Outcome);
    }

    [Fact]
    public void Archive_rejects_mutation_missing_runs_and_unsafe_identifiers()
    {
        Directory.CreateDirectory(repositoryRoot);
        var store = new ReviewRunArchiveStore(repositoryRoot);
        var createdAt = new DateTimeOffset(2026, 8, 12, 23, 0, 0, TimeSpan.Zero);
        var run = CreateRun("run-create-only", createdAt);
        store.CreateRun(run);

        Assert.False(store.TryFindRun("missing", out var missingDirectory));
        Assert.Equal(string.Empty, missingDirectory);
        Assert.Throws<IOException>(() => store.CreateRun(run));
        Assert.Throws<DirectoryNotFoundException>(() => store.LoadRun("missing"));
        Assert.Throws<DirectoryNotFoundException>(() => store.AppendOperation(new RunOperationRecord
        {
            RunId = "missing",
            OperationId = "op-missing",
            Attempt = 1,
            Ordinal = 1,
            Path = "Subject.cs",
            Level = "file",
            State = "failed",
        }));
        Assert.Throws<ArgumentException>(() => store.WriteAttempt(
            CreateAttempt(run.RunId, 0, createdAt, "failed", "partial")));
        Assert.Throws<ArgumentException>(() => store.TryFindRun("../escape", out _));
        Assert.Throws<ArgumentException>(() => new ReviewRunArchiveStore(" "));
        Assert.Throws<ArgumentNullException>(() => store.CreateRun(null!));
        Assert.Throws<ArgumentNullException>(() => store.AppendOperation(null!));
        Assert.Throws<ArgumentNullException>(() => store.AppendFinding(null!));
        Assert.Throws<ArgumentNullException>(() => store.WriteAttempt(null!));
    }

    [Fact]
    public void Archive_json_uses_the_versioned_contract_and_rejects_null_documents()
    {
        var run = CreateRun("run-json", DateTimeOffset.UnixEpoch);

        var document = ReviewRunArchiveJson.Serialize(run);
        var line = ReviewRunArchiveJson.SerializeLine(run);

        Assert.Contains("\n", document, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        Assert.Contains(RunRecord.SchemaId, document, StringComparison.Ordinal);
        AssertRunEquivalent(run, ReviewRunArchiveJson.Deserialize<RunRecord>(document));
        Assert.Throws<JsonException>(() => ReviewRunArchiveJson.Deserialize<RunRecord>("null"));
        Assert.Throws<JsonException>(() => ReviewRunArchiveJson.Deserialize<RunRecord>("{"));
        Assert.Throws<ArgumentNullException>(() => ReviewRunArchiveJson.Serialize<RunRecord>(null!));
        Assert.Throws<ArgumentNullException>(() => ReviewRunArchiveJson.SerializeLine<RunRecord>(null!));
    }

    private static RunRecord CreateRun(string runId, DateTimeOffset createdAt) => new()
    {
        RunId = runId,
        RepositoryId = "default",
        CreatedAt = createdAt,
        NodeId = "file-subject",
        NodePath = "src/Subject.cs",
        Level = "file",
        Kind = "code",
        Targets = [new RunRecordTarget("file-subject", "src/Subject.cs", "sha256:" + new string('c', 64))],
        CliType = "codex",
        Model = "gpt-5.6-sol",
        ThinkingLevel = "high",
        Force = true,
        TokenCap = 2_000,
        CostCap = 1.25m,
        SourceCommit = new string('d', 40),
        SourceDirty = false,
    };

    private static RunAttemptRecord CreateAttempt(
        string runId,
        int attempt,
        DateTimeOffset startedAt,
        string outcome,
        string completeness) => new()
    {
        RunId = runId,
        Attempt = attempt,
        Outcome = outcome,
        Completeness = completeness,
        StartedAt = startedAt,
        FinishedAt = startedAt.AddSeconds(30),
        ArchivedAt = startedAt.AddSeconds(31),
        CompletedOperations = 1,
        FailedOperations = 0,
        SkippedOperations = outcome == "capped" ? 1 : 0,
        Usage = new TokenUsage(500, 100, 25, 10, 1_500),
        CostSpent = 0.04m,
        Currency = "USD",
        StopReason = outcome == "capped" ? "Token cap reached." : null,
        ErrorCodes = outcome == "capped" ? ["token-cap"] : null,
        UsageLedgerMonths = ["2026-08"],
    };

    private static void AssertRunEquivalent(RunRecord expected, RunRecord actual)
    {
        Assert.Equal(expected with { Targets = actual.Targets }, actual);
        Assert.Equal(expected.Targets.ToArray(), actual.Targets.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(repositoryRoot)) Directory.Delete(repositoryRoot, recursive: true);
    }
}
