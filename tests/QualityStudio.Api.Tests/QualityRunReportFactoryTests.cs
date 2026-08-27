using AgentOrchestrator.CodeQuality;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class QualityRunReportFactoryTests
{
    [Fact]
    public void Compare_categorizes_findings_by_fingerprint_and_labels_the_pair_exact()
    {
        var baseline = CreateReport("run-a", model: "claude-sonnet-5", findings:
        [
            Finding("persisting", "open"),
            Finding("resolved-only-in-baseline", "open"),
        ]);
        var current = CreateReport("run-b", model: "claude-sonnet-5", findings:
        [
            Finding("persisting", "accepted"),
            Finding("new-in-current", "open"),
        ]);

        var comparison = QualityRunReportFactory.Compare(baseline, current);

        Assert.Equal("run-a", comparison.FromRunId);
        Assert.Equal("run-b", comparison.ToRunId);
        Assert.Equal(["exact"], comparison.ComparabilityLabels);
        Assert.Equal("available", comparison.Delta.Status);
        Assert.Equal(["sha256:new-in-current"], comparison.Delta.New);
        Assert.Equal(["sha256:persisting"], comparison.Delta.Persisting);
        Assert.Equal(["sha256:resolved-only-in-baseline"], comparison.Delta.Resolved);
        Assert.Equal(["sha256:persisting"], comparison.Delta.StateChanged);
    }

    [Fact]
    public void Compare_labels_a_model_change_without_folding_it_into_the_finding_delta()
    {
        var baseline = CreateReport("run-a", model: "claude-sonnet-5", findings: [Finding("stable", "open")]);
        var current = CreateReport("run-b", model: "claude-opus-4-8", findings: [Finding("stable", "open")]);

        var comparison = QualityRunReportFactory.Compare(baseline, current);

        Assert.Equal(["model-changed"], comparison.ComparabilityLabels);
        Assert.Empty(comparison.Delta.New);
        Assert.Empty(comparison.Delta.Resolved);
        Assert.Empty(comparison.Delta.StateChanged);
        Assert.Equal(["sha256:stable"], comparison.Delta.Persisting);
    }

    [Fact]
    public void Compare_labels_an_input_change_when_the_subject_manifest_hash_differs()
    {
        var baseline = CreateReport("run-a", model: "claude-sonnet-5", findings: [], subjectHash: "sha256:" + new string('a', 64));
        var current = CreateReport("run-b", model: "claude-sonnet-5", findings: [], subjectHash: "sha256:" + new string('b', 64));

        var comparison = QualityRunReportFactory.Compare(baseline, current);

        Assert.Equal(["inputs-changed"], comparison.ComparabilityLabels);
    }

    [Fact]
    public void Compare_labels_incomplete_runs_and_still_returns_a_delta()
    {
        var baseline = CreateReport("run-a", model: "claude-sonnet-5", findings: [], completeness: "partial");
        var current = CreateReport("run-b", model: "claude-sonnet-5", findings: []);

        var comparison = QualityRunReportFactory.Compare(baseline, current);

        Assert.Equal(["incomplete"], comparison.ComparabilityLabels);
        Assert.Equal("available", comparison.Delta.Status);
    }

    [Theory]
    [InlineData("other-repo", "code", "unit-project", "project")]
    [InlineData("default", "security", "unit-project", "project")]
    [InlineData("default", "code", "other-unit", "project")]
    [InlineData("default", "code", "unit-project", "file")]
    public void Compare_rejects_runs_that_are_not_the_same_repository_kind_scope_and_level(
        string repositoryId, string kind, string scopeUnitId, string level)
    {
        var baseline = CreateReport("run-a", model: "claude-sonnet-5", findings: []);
        var current = CreateReport("run-b", model: "claude-sonnet-5", findings: []) with
        {
            Run = CreateReport("run-b", model: "claude-sonnet-5", findings: []).Run with
            {
                RepositoryId = repositoryId,
                Kind = kind,
                ScopeUnitId = scopeUnitId,
                Level = level,
            },
        };

        Assert.Throws<ArgumentException>(() => QualityRunReportFactory.Compare(baseline, current));
    }

    private static QualityRunFinding Finding(string fingerprintSeed, string state) => new(
        $"finding-{fingerprintSeed}", "quality.rule.demo", "correctness", "medium", state,
        $"Finding {fingerprintSeed}", "Description", "Recommendation", null,
        "sha256:" + fingerprintSeed,
        [new QualityFindingLocation("src/App.cs", 1, 1, 1, 8)], "agent", null, null);

    private static QualityRunReportDocument CreateReport(
        string id,
        string model,
        IReadOnlyList<QualityRunFinding> findings,
        string completeness = "complete",
        string? subjectHash = null)
    {
        var run = new QualityRunIdentity(
            id, 1, "default", "Fixture repository", "code", "unit-project", "project", ".",
            completeness == "complete" ? "done" : "capped", completeness,
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            model, "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget(
            "unit-file", "App.cs", "src/App.cs", subjectHash ?? "sha256:" + new string('a', 64));
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["critical"] = 0, ["high"] = 0,
            ["medium"] = findings.Count(finding => finding.Severity == "medium"),
            ["low"] = 0, ["info"] = 0,
        };
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            run,
            new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]),
            new QualityRunExecution(1, 0, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [new QualityRunObservation(
                "unit-project", "project", ".", "done", true,
                ".quality/reviews/projects/root.review-meta.code.json", "sha256:" + new string('b', 64),
                run.FinishedAt, "sha256:" + new string('c', 64), "provider-run",
                new QualityRunGrade(85, "B", "Fixture grade."), "Fixture summary.", findings)],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                85, "B",
                new QualityRunFindingCounts(findings.Count, bySeverity,
                    new Dictionary<string, int> { ["open"] = findings.Count(finding => finding.State == "open") }),
                findings.Count > 0 ? "medium" : null, null));
    }
}
