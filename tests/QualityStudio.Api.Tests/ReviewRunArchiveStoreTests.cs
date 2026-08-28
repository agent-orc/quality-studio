using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly DateTimeOffset RunCreatedAt = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Run_operation_finding_and_attempt_records_validate_against_their_v1_schemas()
    {
        var run = CreateRun("run-schema-1");
        var operation = CreateOperation(run.RunId, attempt: 1, ordinal: 0);
        var finding = CreateFinding(run.RunId, operation.OperationId);
        var attempt = CreateAttempt(run.RunId, attemptNumber: 1, outcome: "done");

        AssertValidatesAgainstSchema(run, "run-record.v1.schema.json");
        AssertValidatesAgainstSchema(operation, "run-operation.v1.schema.json");
        AssertValidatesAgainstSchema(finding, "run-finding.v1.schema.json");
        AssertValidatesAgainstSchema(attempt, "run-attempt.v1.schema.json");
    }

    [Fact]
    public void CreateRun_rejects_a_second_call_for_the_same_run_id()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);
        var run = CreateRun("run-create-only");

        store.CreateRun(run);

        Assert.Throws<IOException>(() => store.CreateRun(run));
    }

    [Fact]
    public void WriteAttempt_rejects_a_second_call_for_the_same_attempt_number()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);
        store.CreateRun(CreateRun("run-attempt-overwrite"));
        var attempt = CreateAttempt("run-attempt-overwrite", attemptNumber: 1, outcome: "capped");

        store.WriteAttempt(attempt);

        Assert.Throws<IOException>(() => store.WriteAttempt(attempt));
    }

    [Fact]
    public void A_capped_run_that_resumes_keeps_both_attempts_readable_and_never_rewrites_the_first()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);
        var runId = "run-capped-then-resumed";
        store.CreateRun(CreateRun(runId));

        var capped = CreateAttempt(runId, attemptNumber: 1, outcome: "capped");
        store.WriteAttempt(capped);
        store.AppendOperation(CreateOperation(runId, attempt: 1, ordinal: 0));

        var resumed = CreateAttempt(runId, attemptNumber: 2, outcome: "done");
        store.WriteAttempt(resumed);
        store.AppendOperation(CreateOperation(runId, attempt: 2, ordinal: 1));

        var archive = store.LoadRun(runId);
        Assert.NotNull(archive);
        Assert.Equal(2, archive!.Attempts.Count);
        Assert.Equal(1, archive.Attempts[0].Attempt);
        Assert.Equal("capped", archive.Attempts[0].Outcome);
        Assert.Equal(2, archive.Attempts[1].Attempt);
        Assert.Equal("done", archive.Attempts[1].Outcome);
        Assert.Equal(2, archive.Operations.Count);
        Assert.Equal([1, 2], archive.Operations.Select(operation => operation.Attempt).ToArray());
    }

    [Fact]
    public void AppendFinding_round_trips_through_the_archive_reader()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);
        var runId = "run-findings";
        store.CreateRun(CreateRun(runId));
        var operation = CreateOperation(runId, attempt: 1, ordinal: 0);
        store.AppendOperation(operation);
        store.AppendFinding(CreateFinding(runId, operation.OperationId));

        var archive = store.LoadRun(runId);

        var finding = Assert.Single(archive!.Findings);
        Assert.Equal("fp-1", finding.Fingerprint);
        Assert.Equal(FindingSeverity.High, finding.Severity);
    }

    [Fact]
    public void LoadRun_returns_null_for_an_unknown_run()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.Null(store.LoadRun("does-not-exist"));
    }

    [Fact]
    public void Operations_and_attempts_cannot_be_written_before_the_run_is_created()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.Throws<InvalidOperationException>(() =>
            store.AppendOperation(CreateOperation("run-missing", attempt: 1, ordinal: 0)));
        Assert.Throws<InvalidOperationException>(() =>
            store.WriteAttempt(CreateAttempt("run-missing", attemptNumber: 1, outcome: "done")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/run")]
    [InlineData("")]
    public void A_run_id_containing_path_separators_or_traversal_is_rejected(string runId)
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);

        Assert.ThrowsAny<ArgumentException>(() => store.CreateRun(CreateRun(runId)));
    }

    [Fact]
    public void The_archive_directory_stays_confined_to_the_repository_run_history_root()
    {
        using var fixture = TemporaryRepository.Create();
        var store = new ReviewRunArchiveStore(fixture.Root);
        store.CreateRun(CreateRun("run-confinement"));

        var monthDirectory = Directory.EnumerateDirectories(store.ArchiveRoot).Single();
        var runDirectory = Path.Combine(monthDirectory, "run-confinement");
        Assert.True(PathConfinementIsWithin(store.ArchiveRoot, runDirectory));
    }

    private static bool PathConfinementIsWithin(string root, string candidate)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static RunRecord CreateRun(string runId) => new()
    {
        RunId = runId,
        RepositoryId = "default",
        CreatedAt = RunCreatedAt,
        Subject = new RunArchiveSubject("unit-1", "src/Sample.cs", "file"),
        Kind = "code",
        Targets = [new RunArchiveTarget("unit-1", "Sample.cs", "src/Sample.cs", "sha256:" + new string('a', 64))],
        Configuration = new RunArchiveConfiguration(Model: "test-model", CliType: "test-agent", ThinkingLevel: null, Force: true),
    };

    private static RunOperationRecord CreateOperation(string runId, int attempt, int ordinal) => new()
    {
        RunId = runId,
        OperationId = $"{runId}-op-{ordinal}",
        Ordinal = ordinal,
        Attempt = attempt,
        UnitId = "unit-1",
        Path = "src/Sample.cs",
        Level = "file",
        State = "done",
        StartedAt = RunCreatedAt.AddMinutes(ordinal),
        FinishedAt = RunCreatedAt.AddMinutes(ordinal).AddSeconds(30),
        Verdict = new RunOperationVerdict { Kind = "grade", Grade = new GradeSnapshot(92, "A") },
    };

    private static RunFindingRecord CreateFinding(string runId, string operationId) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Fingerprint = "fp-1",
        FindingId = "finding-1",
        RuleId = "rule.sample",
        Severity = FindingSeverity.High,
        Title = "Sample finding",
        Locations = [new FindingLocation("src/Sample.cs")],
        State = "open",
        ObservedAt = RunCreatedAt.AddMinutes(1),
    };

    private static RunAttemptRecord CreateAttempt(string runId, int attemptNumber, string outcome) => new()
    {
        RunId = runId,
        Attempt = attemptNumber,
        Outcome = outcome,
        Counters = new RunAttemptCounters(TotalFiles: 1, CompletedFiles: outcome == "done" ? 1 : 0, FailedFiles: 0,
            SkippedFiles: 0),
        StartedAt = RunCreatedAt.AddMinutes(attemptNumber),
        FinishedAt = RunCreatedAt.AddMinutes(attemptNumber).AddMinutes(5),
    };

    private static void AssertValidatesAgainstSchema<T>(T record, string schemaFileName)
    {
        var json = JsonSerializer.Serialize(record, ReviewMetaJson.Options);
        using var document = JsonDocument.Parse(json);
        var schema = JsonSchema.FromText(File.ReadAllText(
            Path.Combine(RepositoryRoot.Find(), "schemas", schemaFileName)));
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static class RepositoryRoot
    {
        private const string Marker = "QualityStudio.slnx";

        public static string Find()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, Marker))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException($"Could not locate {Marker} above {AppContext.BaseDirectory}.");
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public string Root { get; }

        private TemporaryRepository(string root) => Root = root;

        public static TemporaryRepository Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "qs-archive-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TemporaryRepository(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
