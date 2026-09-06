using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Journals were kept forever while their reports were pruned to the newest 50, so start-up recovery
/// rebuilt exactly the reports the last prune deleted, on every restart. These tests pin both halves of
/// the fix: journals follow the same retention, and recovery does not resurrect a pruned report.
/// </summary>
public sealed class ReviewRunRetentionTests
{
    [Fact]
    public void Journal_retention_keeps_the_newest_terminal_runs_and_never_touches_a_resumable_one()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-journal-prune-").FullName;
        try
        {
            var store = new ReviewRunStore(root);
            var origin = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 5; index++) CreateJournal(store, $"run-{index:00}", "done", origin.AddHours(index));
            CreateJournal(store, "run-running", "running", origin.AddHours(-10));
            CreateJournal(store, "run-pinned", "done", origin.AddHours(-20));

            var pruned = store.Prune(keep: 2, pinnedRunIds: new HashSet<string>(StringComparer.Ordinal) { "run-pinned" });

            Assert.Equal(3, pruned.Removed);
            Assert.Equal(["run-03", "run-04", "run-pinned", "run-running"],
                store.LoadAll().Select(run => run.Manifest.RunId).Order(StringComparer.Ordinal));
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Journal_retention_reports_a_directory_it_cannot_remove_instead_of_failing_the_host()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-journal-prune-error-").FullName;
        try
        {
            var store = new ReviewRunStore(root);
            var origin = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 3; index++) CreateJournal(store, $"run-{index:00}", "done", origin.AddHours(index));
            var failures = new List<string>();

            using (File.Open(Path.Combine(store.RunsPath, "run-00", "status.json"), FileMode.Open,
                       FileAccess.Read, FileShare.None))
            {
                var pruned = store.Prune(keep: 0, pinnedRunIds: new HashSet<string>(StringComparer.Ordinal),
                    (directory, _) => failures.Add(Path.GetFileName(directory)));
                Assert.Equal(2, pruned.Removed);
            }

            // Windows refuses to delete a directory whose file is open; Linux allows it. Either way the
            // remaining journals were pruned and the failure, if any, was reported rather than thrown.
            Assert.True(failures.Count is 0 or 1);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Recovery_does_not_republish_reports_that_retention_removed()
    {
        const int keep = QualityRunReportStore.DefaultRetentionKeep;
        const int total = keep + 5;
        var testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-retention-tests",
            Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(testRoot, "repository");
        var hostRoot = Path.Combine(testRoot, "host");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"), "public class Sample { }",
            TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryRoot);

        var runStore = new ReviewRunStore(repositoryRoot);
        var reportStore = new QualityRunReportStore(repositoryRoot);
        var origin = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var runIds = Enumerable.Range(0, total).Select(index => $"review-{index:000}").ToArray();
        for (var index = 0; index < total; index++)
        {
            CreateJournal(runStore, runIds[index], "done", origin.AddHours(index));
            var report = CreateReport(runIds[index]);
            reportStore.Save(report with { Run = report.Run with { FinishedAt = origin.AddHours(index) } });
        }
        var evicted = reportStore.Prune(keep, new HashSet<string>(StringComparer.Ordinal))
            .Removed;
        Assert.Equal(total - keep, evicted);

        try
        {
            await using var application = new RetentionApplication(repositoryRoot, hostRoot);
            using var client = application.CreateClient();
            // Any request forces the host to build, which starts the review job service and its recovery.
            using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
            health.EnsureSuccessStatusCode();
            await WaitForJournalPruneAsync(runStore, keep, TestContext.Current.CancellationToken);

            var reports = reportStore.LoadAll().Select(report => report.Run.Id).Order(StringComparer.Ordinal).ToArray();
            var journals = runStore.LoadAll().Select(run => run.Manifest.RunId).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(keep, reports.Length);
            Assert.Equal(runIds.Skip(total - keep).Order(StringComparer.Ordinal), reports);
            Assert.Equal(keep, journals.Length);
            Assert.Equal(runIds.Skip(total - keep).Order(StringComparer.Ordinal), journals);
        }
        finally
        {
            try { Directory.Delete(testRoot, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task WaitForJournalPruneAsync(ReviewRunStore store, int keep, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && store.LoadAll().Count > keep; attempt++)
            await Task.Delay(50, cancellationToken);
    }

    private static void CreateJournal(ReviewRunStore store, string runId, string state, DateTimeOffset finishedAt)
    {
        var terminal = ReviewRunStore.IsTerminal(state);
        var manifest = new ReviewRunManifest(
            runId,
            RepositoryRegistry.DefaultRepositoryId,
            new ReviewRunPlanNode("file-sample", "Sample.cs", "Sample.cs"),
            "file",
            "code",
            null,
            "adapter-that-does-not-exist",
            finishedAt.AddMinutes(-1),
            [new ReviewRunPlanTarget("file-sample", "Sample.cs", "Sample.cs",
                "sha256:" + new string('a', 64))],
            null);
        store.Create(manifest, new ReviewRunStatus(
            runId,
            state,
            1,
            terminal ? 1 : 0,
            0,
            terminal ? 1 : 0,
            finishedAt.AddMinutes(-1),
            finishedAt.AddMinutes(-1),
            terminal ? finishedAt : null,
            [],
            0,
            new TokenUsage(null, null, null, null, 0)));
    }

    private static QualityRunReportDocument CreateReport(string runId)
    {
        var run = new QualityRunIdentity(
            runId, 1, "default", "Fixture repository", "code", "unit-file", "file", "Sample.cs",
            "done", "complete",
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 8, 2, 0, TimeSpan.Zero),
            "gpt-5.6-sol", "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget("unit-file", "Sample.cs", "Sample.cs", "sha256:" + new string('a', 64));
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            run,
            new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]),
            new QualityRunExecution(1, 1, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                85, "B",
                new QualityRunFindingCounts(0,
                    new Dictionary<string, int> { ["critical"] = 0, ["high"] = 0, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                    new Dictionary<string, int>()),
                null, null));
    }

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
            })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static class TestDirectory
    {
        public static void Delete(string root)
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class RetentionApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = root,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }
}
