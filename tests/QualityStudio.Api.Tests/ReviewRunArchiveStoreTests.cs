using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunArchiveStoreTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    [Fact]
    public void Archive_round_trips_two_stopped_attempts_and_schema_fixtures_validate()
    {
        using var fixture = new ArchiveFixture();
        var run = CreateRun();
        fixture.Store.CreateRun(run);

        var operation = CreateOperation(run.RunId);
        var finding = CreateFinding(run.RunId, operation.OperationId);
        fixture.Store.AppendOperation(run.CreatedAt, operation);
        fixture.Store.AppendFinding(run.CreatedAt, finding);
        fixture.Store.CreateAttempt(run.CreatedAt, CreateAttempt(run.RunId, 1, "capped", false, [operation.OperationId]));
        fixture.Store.CreateAttempt(run.CreatedAt, CreateAttempt(run.RunId, 2, "done", true, [operation.OperationId]));

        var loaded = fixture.Store.Load(run.CreatedAt, run.RunId);

        Assert.Equal(run.RunId, loaded.Run.RunId);
        Assert.Equal(run.RepositoryId, loaded.Run.RepositoryId);
        Assert.Equal(run.CreatedAt, loaded.Run.CreatedAt);
        Assert.Equal(run.Subject, loaded.Run.Subject);
        Assert.Equal(run.Configuration, loaded.Run.Configuration);
        Assert.Equal(run.Targets, loaded.Run.Targets);
        Assert.Equal(operation, Assert.Single(loaded.Operations));
        var loadedFinding = Assert.Single(loaded.Findings);
        Assert.Equal(finding.Fingerprint, loadedFinding.Fingerprint);
        Assert.Equal(finding.OperationId, loadedFinding.OperationId);
        Assert.Equal(finding.Locations, loadedFinding.Locations);
        Assert.Equal([1, 2], loaded.Attempts.Select(attempt => attempt.Attempt));
        Assert.Equal("capped", loaded.Attempts[0].Outcome);
        Assert.Equal("done", loaded.Attempts[1].Outcome);

        Validate(Path.Combine(fixture.RunDirectory(run), "run.json"), "run-record.v1.schema.json");
        ValidateLine(Path.Combine(fixture.RunDirectory(run), "operations.jsonl"), "run-operation.v1.schema.json");
        ValidateLine(Path.Combine(fixture.RunDirectory(run), "findings.jsonl"), "run-finding.v1.schema.json");
        Validate(Path.Combine(fixture.RunDirectory(run), "attempts", "0001.json"), "run-attempt.v1.schema.json");
        Validate(Path.Combine(fixture.RunDirectory(run), "attempts", "0002.json"), "run-attempt.v1.schema.json");
    }

    [Fact]
    public void Immutable_archive_documents_reject_overwrite()
    {
        using var fixture = new ArchiveFixture();
        var run = CreateRun();
        fixture.Store.CreateRun(run);
        var attempt = CreateAttempt(run.RunId, 1, "capped", false, []);
        fixture.Store.CreateAttempt(run.CreatedAt, attempt);

        Assert.Throws<IOException>(() => fixture.Store.CreateRun(run));
        Assert.Throws<IOException>(() => fixture.Store.CreateAttempt(run.CreatedAt, attempt));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/run")]
    [InlineData(".")]
    [InlineData("..")]
    public void Run_ids_are_confined_to_one_repository_path_component(string runId)
    {
        using var fixture = new ArchiveFixture();
        Assert.Throws<ArgumentException>(() => fixture.Store.CreateRun(CreateRun() with { RunId = runId }));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "escape")));
    }

    [Fact]
    public void Archive_paths_cannot_traverse_a_symbolic_link_outside_the_repository()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new ArchiveFixture();
        var outside = Directory.CreateTempSubdirectory("quality-studio-run-archive-outside-");
        try
        {
            var qualityDirectory = Path.Combine(fixture.Root, ".quality");
            Directory.CreateDirectory(qualityDirectory);
            Directory.CreateSymbolicLink(Path.Combine(qualityDirectory, "run-history"), outside.FullName);

            var exception = Assert.Throws<ArgumentException>(() => fixture.Store.CreateRun(CreateRun()));

            Assert.Contains("symbolic links", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.FullName));
        }
        finally
        {
            Directory.Delete(outside.FullName, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_archive_lines_are_reported_instead_of_omitted()
    {
        using var fixture = new ArchiveFixture();
        var run = CreateRun();
        fixture.Store.CreateRun(run);
        File.WriteAllText(Path.Combine(fixture.RunDirectory(run), "operations.jsonl"), "{\"operationId\":\n");

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Store.Load(run.CreatedAt, run.RunId));

        Assert.Contains("operations.jsonl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_invalid_but_well_formed_attempt_is_reported_as_history_corrupt()
    {
        using var fixture = new ArchiveFixture();
        var run = CreateRun();
        fixture.Store.CreateRun(run);
        fixture.Store.CreateAttempt(run.CreatedAt, CreateAttempt(run.RunId, 1, "done", true, []));
        var attemptPath = Path.Combine(fixture.RunDirectory(run), "attempts", "0001.json");
        File.WriteAllText(attemptPath, File.ReadAllText(attemptPath).Replace(
            "\"outcome\": \"done\"", "\"outcome\": \"invented\"", StringComparison.Ordinal));

        var result = Assert.Single(fixture.Store.LoadAll());

        Assert.Equal("history-corrupt", result.ErrorCode);
        Assert.Null(result.Archive);
    }

    [Fact]
    public void History_reader_pages_filters_selects_attempts_and_keeps_corrupt_rows_visible()
    {
        using var fixture = new ArchiveFixture();
        var older = CreateRun("review-older", CreatedAt, "code", "src");
        fixture.Store.CreateRun(older);
        var olderOperation = CreateOperation(older.RunId);
        fixture.Store.AppendOperation(older.CreatedAt, olderOperation);
        fixture.Store.CreateAttempt(older.CreatedAt,
            CreateAttempt(older.RunId, 1, "capped", false, [olderOperation.OperationId]));
        fixture.Store.CreateAttempt(older.CreatedAt,
            CreateAttempt(older.RunId, 2, "done", true, [olderOperation.OperationId]));

        var newer = CreateRun("review-newer", CreatedAt.AddDays(1), "security", ".");
        fixture.Store.CreateRun(newer);
        fixture.Store.CreateAttempt(newer.CreatedAt, CreateAttempt(newer.RunId, 1, "done", true, []));

        var active = CreateRun("review-active", CreatedAt.AddDays(2), "code", "src");
        fixture.Store.CreateRun(active);

        var corruptDirectory = Path.Combine(fixture.Store.HistoryPath, "2026-08", "review-corrupt");
        Directory.CreateDirectory(corruptDirectory);
        File.WriteAllText(Path.Combine(corruptDirectory, "run.json"), "{\"schemaVersion\":");

        var first = ReviewRunHistoryReader.List(fixture.Store, "default", null, 1, null, null, null);
        Assert.Equal("review-newer", Assert.Single(first.Runs).RunId);
        Assert.NotNull(first.NextCursor);
        var second = ReviewRunHistoryReader.List(fixture.Store, "default", first.NextCursor, 1, null, null, null);
        Assert.Equal("review-older", Assert.Single(second.Runs).RunId);

        var filtered = ReviewRunHistoryReader.List(fixture.Store, "default", null, 20, "security", null, "done");
        Assert.Contains(filtered.Runs, row => row.RunId == "review-newer");
        Assert.Contains(filtered.Runs, row => row.RunId == "review-corrupt" && row.ErrorCode == "history-corrupt");
        Assert.DoesNotContain(filtered.Runs, row => row.RunId == "review-older");
        Assert.DoesNotContain(ReviewRunHistoryReader.List(fixture.Store, "default", null, 20, null, null, null).Runs,
            row => row.RunId == "review-active");

        var detail = ReviewRunHistoryReader.Get(fixture.Store, "default", older.RunId, attempt: 1);
        Assert.Equal(1, detail.Attempt.Attempt);
        Assert.Single(detail.Operations);
        Assert.Throws<ReviewRunHistoryCorruptException>(() =>
            ReviewRunHistoryReader.Get(fixture.Store, "default", "review-corrupt"));
    }

    private static ReviewRunArchiveRecord CreateRun(
        string runId = "review-archive-test",
        DateTimeOffset? createdAt = null,
        string kind = "code",
        string path = ".") => new()
    {
        RunId = runId,
        RepositoryId = "default",
        CreatedAt = createdAt ?? CreatedAt,
        Subject = new ReviewRunPlanNode("project-test", "Test", path),
        Level = "project",
        Kind = kind,
        Targets =
        [
            new ReviewRunPlanTarget("file-a", "A.cs", "src/A.cs",
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
        ],
        Configuration = new ReviewRunArchiveConfiguration(
            "gpt-5", "high", "codex", false, 1000, null,
            new ReviewRunEstimate(1, 2, 100, 25, 5, 0.01m, "USD", "priced", 3, "fixture"),
            new ReviewModelRecommendation("2026-07-24", "gpt-5.6-sol", "xhigh", "frontier", 100,
                "sol-xhigh", "Aggregate security work requires the frontier route.", "fixture"), true),
        SourceRevision = new ReviewRunSourceRevision(
            "0123456789abcdef0123456789abcdef01234567", false),
    };

    private static ReviewRunOperationRecord CreateOperation(string runId) => new()
    {
        RunId = runId,
        OperationId = "operation-0001",
        Ordinal = 1,
        Attempt = 1,
        UnitId = "file-a",
        Path = "src/A.cs",
        Level = "file",
        State = "done",
        StartedAt = CreatedAt.AddMinutes(1),
        FinishedAt = CreatedAt.AddMinutes(2),
        ProviderRunId = "provider-1",
        ReviewedHash = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        ReviewInputsHash = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        ResultSidecar = "src/A.cs.review-meta.json",
        Grade = new ReviewRunArchivedGrade(87, "B"),
    };

    private static ReviewRunFindingRecord CreateFinding(string runId, string operationId) => new()
    {
        RunId = runId,
        OperationId = operationId,
        Fingerprint = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
        FindingId = "finding-1",
        RuleId = "rule-1",
        Severity = "high",
        Title = "A finding",
        Locations = [new FindingLocation("src/A.cs", new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 2)))],
        State = "open",
    };

    private static ReviewRunAttemptRecord CreateAttempt(
        string runId,
        int attempt,
        string outcome,
        bool complete,
        IReadOnlyList<string> operations) => new()
    {
        RunId = runId,
        Attempt = attempt,
        Outcome = outcome,
        Complete = complete,
        StartedAt = CreatedAt.AddMinutes(attempt),
        FinishedAt = CreatedAt.AddMinutes(attempt + 1),
        ArchivedAt = CreatedAt.AddMinutes(attempt + 2),
        Counters = new ReviewRunAttemptCounters(1, complete ? 1 : 0, 0, complete ? 0 : 1, operations.Count),
        Spend = new ReviewRunAttemptSpend(new TokenUsage(25, 5, 0, 0, 100), 0.01m, "USD", "priced"),
        CumulativeCounters = new ReviewRunAttemptCounters(1, complete ? 1 : 0, 0, complete ? 0 : 1, operations.Count),
        CumulativeSpend = new ReviewRunAttemptSpend(new TokenUsage(25, 5, 0, 0, 100), 0.01m, "USD", "priced"),
        ErrorCodes = [],
        LedgerMonths = ["2026-08"],
        OperationIds = operations,
        Quality = new ReviewRunAttemptQualitySummary(87, "B", null, 1, "high"),
    };

    private static void ValidateLine(string path, string schemaFile)
    {
        var line = Assert.Single(File.ReadAllLines(path));
        ValidateJson(line, schemaFile);
    }

    private static void Validate(string path, string schemaFile) => ValidateJson(File.ReadAllText(path), schemaFile);

    private static void ValidateJson(string json, string schemaFile)
    {
        if (!Schemas.TryGetValue(schemaFile, out var schema))
        {
            schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", schemaFile)));
            Schemas.Add(schemaFile, schema);
        }
        using var document = JsonDocument.Parse(json);
        var result = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, result.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class ArchiveFixture : IDisposable
    {
        public ArchiveFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-run-archive-").FullName;
            Store = new ReviewRunArchiveStore(Root);
        }

        public string Root { get; }
        public ReviewRunArchiveStore Store { get; }

        public string RunDirectory(ReviewRunArchiveRecord run) => Path.Combine(
            Store.HistoryPath, run.CreatedAt.UtcDateTime.ToString("yyyy-MM"), run.RunId);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
