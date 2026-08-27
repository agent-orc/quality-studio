namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunComparisonTests
{
    [Fact]
    public void Compatible_runs_align_findings_by_fingerprint_into_four_categories()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            var baseline = CreateReport("run-baseline", finding: BuildFindings(
                Finding("stays-open", "open"),
                Finding("gets-resolved", "open")));
            var candidate = CreateReport("run-candidate", finding: BuildFindings(
                Finding("stays-open", "accepted"),
                Finding("brand-new", "open")));
            store.Save(baseline);
            store.Save(candidate);

            var comparison = QualityRunComparisonBuilder.Build("default", "run-baseline", "run-candidate", store);

            Assert.Equal("ok", comparison.Baseline.Status);
            Assert.Equal("ok", comparison.Candidate.Status);
            Assert.True(comparison.Comparable);
            Assert.NotNull(comparison.Compatibility);
            Assert.True(comparison.Compatibility!.SameScope);
            Assert.True(comparison.Compatibility.RouteMatches);
            Assert.True(comparison.Compatibility.InputsMatch);
            Assert.Empty(comparison.Compatibility.Reasons);

            var delta = comparison.Delta!;
            Assert.Equal("brand-new", Assert.Single(delta.New).Fingerprint);
            Assert.Equal("open", Assert.Single(delta.New).CandidateState);
            Assert.Null(Assert.Single(delta.New).BaselineState);

            Assert.Equal("gets-resolved", Assert.Single(delta.Resolved).Fingerprint);
            Assert.Equal("open", Assert.Single(delta.Resolved).BaselineState);
            Assert.Null(Assert.Single(delta.Resolved).CandidateState);

            var changed = Assert.Single(delta.DispositionChanged);
            Assert.Equal("stays-open", changed.Fingerprint);
            Assert.Equal("open", changed.BaselineState);
            Assert.Equal("accepted", changed.CandidateState);

            Assert.Empty(delta.Unchanged);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Unchanged_findings_with_the_same_disposition_are_reported_as_unchanged()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            store.Save(CreateReport("run-a", finding: BuildFindings(Finding("steady", "accepted"))));
            store.Save(CreateReport("run-b", finding: BuildFindings(Finding("steady", "accepted"))));

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "run-b", store);

            var unchanged = Assert.Single(comparison.Delta!.Unchanged);
            Assert.Equal("steady", unchanged.Fingerprint);
            Assert.Equal("accepted", unchanged.BaselineState);
            Assert.Equal("accepted", unchanged.CandidateState);
            Assert.Empty(comparison.Delta.New);
            Assert.Empty(comparison.Delta.Resolved);
            Assert.Empty(comparison.Delta.DispositionChanged);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Resolved_findings_are_findings_absent_from_the_candidate_outcome_not_merely_filtered()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            store.Save(CreateReport("run-a", finding: BuildFindings(Finding("gone", "open"))));
            store.Save(CreateReport("run-b", finding: BuildFindings(Finding("gone", "resolved"))));

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "run-b", store);

            // The candidate finding is present but marked resolved: it must not count as active/unchanged.
            var resolved = Assert.Single(comparison.Delta!.Resolved);
            Assert.Equal("gone", resolved.Fingerprint);
            Assert.Empty(comparison.Delta.Unchanged);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Missing_run_is_reported_plainly_and_the_comparison_is_not_comparable()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            store.Save(CreateReport("run-a", finding: []));

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "does-not-exist", store);

            Assert.Equal("ok", comparison.Baseline.Status);
            Assert.Equal("missing", comparison.Candidate.Status);
            Assert.NotNull(comparison.Candidate.Error);
            Assert.False(comparison.Comparable);
            Assert.Null(comparison.Compatibility);
            Assert.Null(comparison.Delta);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Corrupt_snapshot_is_reported_plainly_and_survives_a_later_sidecar_replacement()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            store.Save(CreateReport("run-a", finding: []));
            Directory.CreateDirectory(store.ReportsPath);
            File.WriteAllText(store.PathFor("run-b"), "{ not json");

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "run-b", store);

            Assert.Equal("corrupt", comparison.Candidate.Status);
            Assert.NotNull(comparison.Candidate.Error);
            Assert.False(comparison.Comparable);

            // A corrupt sibling snapshot must not affect reads of the other, valid one, including
            // after the store is recreated (simulating an API restart).
            var reloaded = new QualityRunReportStore(root);
            Assert.Equal("run-a", reloaded.Load("run-a").Run.Id);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void A_run_belonging_to_another_repository_is_treated_as_missing_not_disclosed()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            store.Save(CreateReport("run-a", finding: []));
            store.Save(CreateReport("run-other-repo", finding: [], repositoryId: "another-repo"));

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "run-other-repo", store);

            Assert.Equal("missing", comparison.Candidate.Status);
            Assert.False(comparison.Comparable);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public void Route_and_input_mismatches_flag_the_comparison_as_not_causally_attributable_but_still_compute_a_delta()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-compare-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            var baseline = CreateReport("run-a", finding: BuildFindings(Finding("f1", "open")), model: "gpt-terra");
            var candidate = CreateReport("run-b", finding: BuildFindings(Finding("f2", "open")), model: "gpt-sol",
                subjectHash: "different-subject-hash");
            store.Save(baseline);
            store.Save(candidate);

            var comparison = QualityRunComparisonBuilder.Build("default", "run-a", "run-b", store);

            Assert.True(comparison.Comparable);
            Assert.False(comparison.Compatibility!.RouteMatches);
            Assert.False(comparison.Compatibility.InputsMatch);
            Assert.True(comparison.Compatibility.SameScope);
            Assert.Contains("Route changed.", comparison.Compatibility.Reasons);
            Assert.Contains("Inputs changed.", comparison.Compatibility.Reasons);
            // The delta is still produced; the caller is responsible for not attributing it to the model alone.
            Assert.NotNull(comparison.Delta);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    private static (string Fingerprint, string State) Finding(string fingerprint, string state) => (fingerprint, state);

    private static QualityRunFinding[] BuildFindings(params (string Fingerprint, string State)[] findings) =>
        findings.Select(item => new QualityRunFinding(
            $"finding-{item.Fingerprint}",
            "quality.rule.demo",
            "correctness",
            "medium",
            item.State,
            $"Finding {item.Fingerprint}",
            "Description",
            "Recommendation",
            null,
            item.Fingerprint,
            [new QualityFindingLocation("src/App.cs", 1, 1, 1, 8)],
            "agent",
            null,
            null)).ToArray();

    private static QualityRunReportDocument CreateReport(
        string id,
        QualityRunFinding[] finding,
        string repositoryId = "default",
        string model = "gpt-5.6-sol",
        string subjectHash = "constant-subject-hash")
    {
        var run = new QualityRunIdentity(
            id, 1, repositoryId, "Fixture repository", "code", "unit-project", "project", ".",
            "done", "complete",
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            model, "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget("unit-file", "App.cs", "src/App.cs", "sha256:" + new string('a', 64));
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["critical"] = 0, ["high"] = 0,
            ["medium"] = finding.Count(item => item.State is "open" or "accepted"),
            ["low"] = 0, ["info"] = 0,
        };
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId, 1, run,
            new QualityRunSubject(subjectHash, [target]),
            new QualityRunExecution(1, 0, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [new QualityRunObservation(
                "unit-project", "project", ".", "done", true,
                ".quality/reviews/projects/root.review-meta.code.json", "sha256:" + new string('b', 64),
                run.FinishedAt, "sha256:" + new string('c', 64), "provider-run",
                new QualityRunGrade(85, "B", "Fixture grade."), "Fixture summary.", finding)],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(85, "B",
                new QualityRunFindingCounts(finding.Count(item => item.State is "open" or "accepted"), bySeverity,
                    new Dictionary<string, int> { ["open"] = finding.Count(item => item.State == "open") }),
                finding.Length > 0 ? "medium" : null, null));
    }
}
