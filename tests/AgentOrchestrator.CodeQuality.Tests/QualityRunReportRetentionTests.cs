using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunReportRetentionTests
{
    [Fact]
    public void LoadSafely_reports_missing_and_corrupt_snapshots_plainly()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-safe-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            var missing = store.LoadSafely("does-not-exist");
            Assert.Equal(QualityRunSnapshotStatus.Missing, missing.Status);
            Assert.Null(missing.Report);
            Assert.Null(missing.Error);

            Directory.CreateDirectory(store.ReportsPath);
            File.WriteAllText(Path.Combine(store.ReportsPath, "broken.json"), "{ not json");
            var corrupt = store.LoadSafely("broken");
            Assert.Equal(QualityRunSnapshotStatus.Corrupt, corrupt.Status);
            Assert.Null(corrupt.Report);
            Assert.NotNull(corrupt.Error);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Prune_keeps_newest_and_pinned_snapshots_and_deletes_the_rest()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-prune-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            var origin = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
            for (var index = 0; index < 5; index++)
            {
                var report = CreateReport($"run-{index:00}");
                store.Save(report with { Run = report.Run with { FinishedAt = origin.AddHours(index) } });
            }

            var pinned = new HashSet<string>(StringComparer.Ordinal) { "run-00" };
            var result = store.Prune(keep: 2, pinnedRunIds: pinned);

            Assert.Equal(2, result.Removed);
            Assert.Equal(3, result.Remaining);
            Assert.Equal(1, result.Pinned);
            var remainingIds = store.LoadAll().Select(report => report.Run.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Assert.Equal(["run-00", "run-03", "run-04"], remainingIds);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void MeasureSize_reports_count_total_and_average_bytes()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-size-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            Assert.Equal(0, store.MeasureSize().Count);

            store.Save(CreateReport("run-a"));
            store.Save(CreateReport("run-b"));
            var summary = store.MeasureSize();

            Assert.Equal(2, summary.Count);
            Assert.True(summary.TotalBytes > 0);
            Assert.Equal(summary.TotalBytes / 2, summary.AverageBytes);
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void PinStore_persists_pins_atomically_and_survives_reload()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-pins-").FullName;
        try
        {
            var store = new QualityRunReportPinStore(root);
            Assert.Empty(store.Load());

            store.Pin("run-a");
            var afterPin = store.Pin("run-b");
            Assert.Equal(["run-a", "run-b"], afterPin.OrderBy(id => id, StringComparer.Ordinal).ToArray());

            var reloaded = new QualityRunReportPinStore(root);
            Assert.Equal(["run-a", "run-b"], reloaded.Load().OrderBy(id => id, StringComparer.Ordinal).ToArray());

            var afterUnpin = reloaded.Unpin("run-a");
            Assert.Equal(["run-b"], afterUnpin.ToArray());
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private static QualityRunReportDocument CreateReport(string runId)
    {
        var finding = new QualityRunFinding(
            "finding-0", "quality.rule.0", "correctness", "high", "open",
            "Finding", "Description", "Recommendation", null,
            "sha256:" + new string('1', 64), [new QualityFindingLocation("src/App.cs", 1, 1, 1, 8)],
            "agent", null, null);
        var run = new QualityRunIdentity(
            runId, 1, "default", "Fixture repository", "code", "unit-project", "project", ".",
            "done", "complete",
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            "gpt-5.6-sol", "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget("unit-file", "App.cs", "src/App.cs", "sha256:" + new string('a', 64));
        var subject = new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]);
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            run,
            subject,
            new QualityRunExecution(1, 0, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [new QualityRunObservation(
                "unit-project", "project", ".", "done", true,
                ".quality/reviews/projects/root.review-meta.code.json", "sha256:" + new string('b', 64),
                run.FinishedAt, "sha256:" + new string('c', 64), "provider-run",
                new QualityRunGrade(85, "B", "Fixture grade."), "Fixture summary.", [finding])],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                85, "B",
                new QualityRunFindingCounts(1,
                    new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                    new Dictionary<string, int> { ["open"] = 1 }),
                "high", null));
    }
}
