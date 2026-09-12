namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunComparisonTests
{
    [Fact]
    public void Compare_classifies_findings_as_new_unchanged_resolved_and_disposition_changed()
    {
        var baseline = CreateReport("run-baseline", "gpt-5.6-sol",
        [
            ("sha256:" + new string('1', 64), "open", "high"),
            ("sha256:" + new string('2', 64), "open", "medium"),
            ("sha256:" + new string('3', 64), "open", "low"),
        ]);
        var candidate = CreateReport("run-candidate", "gpt-5.6-sol",
        [
            ("sha256:" + new string('1', 64), "open", "high"),
            ("sha256:" + new string('2', 64), "accepted", "medium"),
            ("sha256:" + new string('4', 64), "open", "critical"),
        ]);

        var comparison = QualityRunComparer.Compare(baseline, candidate);

        Assert.Equal("run-baseline", comparison.BaselineRunId);
        Assert.Equal("run-candidate", comparison.CandidateRunId);
        Assert.True(comparison.Route.Compatible);
        Assert.Empty(comparison.Route.Differences);
        Assert.Equal("sha256:" + new string('4', 64), Assert.Single(comparison.New).Fingerprint);
        Assert.Equal("sha256:" + new string('1', 64), Assert.Single(comparison.Unchanged).Fingerprint);
        Assert.Equal("sha256:" + new string('3', 64), Assert.Single(comparison.Resolved).Fingerprint);
        var changed = Assert.Single(comparison.DispositionChanged);
        Assert.Equal("sha256:" + new string('2', 64), changed.Fingerprint);
        Assert.Equal("open", changed.BaselineState);
        Assert.Equal("accepted", changed.CandidateState);
    }

    [Fact]
    public void Compare_treats_resolved_findings_as_absent_from_the_active_set()
    {
        var baseline = CreateReport("run-baseline", "gpt-5.6-sol",
            [("sha256:" + new string('1', 64), "open", "high")]);
        var candidate = CreateReport("run-candidate", "gpt-5.6-sol",
            [("sha256:" + new string('1', 64), "resolved", "high")]);

        var comparison = QualityRunComparer.Compare(baseline, candidate);

        Assert.Equal("sha256:" + new string('1', 64), Assert.Single(comparison.Resolved).Fingerprint);
        Assert.Empty(comparison.Unchanged);
        Assert.Empty(comparison.DispositionChanged);
    }

    [Fact]
    public void Compare_flags_route_incompatibility_when_model_or_inputs_differ_without_suppressing_findings()
    {
        var baseline = CreateReport("run-baseline", "gpt-5.6-sol",
            [("sha256:" + new string('1', 64), "open", "high")]);
        var candidate = CreateReport("run-candidate", "claude-opus-4-8",
            [("sha256:" + new string('1', 64), "open", "high")]);

        var comparison = QualityRunComparer.Compare(baseline, candidate);

        Assert.False(comparison.Route.Compatible);
        Assert.Contains(comparison.Route.Differences, reason => reason.Contains("Model changed", StringComparison.Ordinal));
        Assert.Single(comparison.Unchanged);
    }

    private static QualityRunReportDocument CreateReport(
        string runId, string model, IReadOnlyList<(string Fingerprint, string State, string Severity)> findings)
    {
        var qualityFindings = findings.Select((finding, index) => new QualityRunFinding(
            $"finding-{index}", $"quality.rule.{index}", "correctness", finding.Severity, finding.State,
            $"Finding {index}", $"Description {index}", $"Recommendation {index}", null,
            finding.Fingerprint, [new QualityFindingLocation("src/App.cs", index + 1, 1, index + 1, 8)],
            "agent", null, null)).ToArray();
        var run = new QualityRunIdentity(
            runId, 1, "default", "Fixture repository", "code", "unit-project", "project", ".",
            "done", "complete",
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            model, "xhigh", "codex", false);
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
                new QualityRunGrade(85, "B", "Fixture grade."), "Fixture summary.", qualityFindings)],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                85, "B",
                new QualityRunFindingCounts(qualityFindings.Length,
                    new Dictionary<string, int> { ["critical"] = 0, ["high"] = 0, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                    new Dictionary<string, int> { ["open"] = qualityFindings.Length }),
                qualityFindings.Length > 0 ? "high" : null, null));
    }
}
