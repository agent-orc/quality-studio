using System.Net;
using System.Text.Json;
using Json.Schema;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunReportTests
{
    private static readonly Lazy<JsonSchema> RunReportSchema = new(() => JsonSchema.FromText(File.ReadAllText(
        Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-run-report.v1.schema.json"))));

    [Fact]
    public void Canonical_json_validates_and_sarif_and_html_preserve_portable_evidence()
    {
        var report = CreateReport("review-export", findingCount: 21);
        using var canonical = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Json));
        var reportValidation = RunReportSchema.Value.Evaluate(canonical.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(reportValidation.IsValid, reportValidation.ToString());

        using var sarif = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Sarif));
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var run = Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray());
        Assert.StartsWith("quality-studio/default/code/", run.GetProperty("automationDetails").GetProperty("id").GetString(), StringComparison.Ordinal);
        var result = Assert.Single(run.GetProperty("results").EnumerateArray(), item =>
            item.GetProperty("ruleId").GetString() == "quality.rule.0");
        var fingerprints = result.GetProperty("partialFingerprints");
        Assert.Equal(fingerprints.GetProperty("qualityStudioFingerprint/v1").GetString(),
            fingerprints.GetProperty("primaryLocationLineHash").GetString());
        Assert.False(result.TryGetProperty("baselineState", out _));

        var waived = report with
        {
            Observations = [report.Observations[0] with
            {
                Findings = report.Observations[0].Findings.Select((finding, index) =>
                    index == 0 ? finding with { State = "waived" } : finding).ToArray(),
            }],
        };
        using var suppressedSarif = JsonDocument.Parse(QualityRunReportRenderer.Render(waived, QualityReportFormat.Sarif));
        var suppressed = Assert.Single(Assert.Single(suppressedSarif.RootElement.GetProperty("runs").EnumerateArray())
            .GetProperty("results").EnumerateArray(), item => item.GetProperty("ruleId").GetString() == "quality.rule.0");
        Assert.Equal("accepted", Assert.Single(suppressed.GetProperty("suppressions").EnumerateArray())
            .GetProperty("status").GetString());

        var html = QualityRunReportRenderer.Render(report, QualityReportFormat.Html);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WebUtility.HtmlEncode("<script>alert(\"x\")</script>"), html, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/secret-repository", html, StringComparison.Ordinal);

        var markdown = QualityRunReportRenderer.Render(report, QualityReportFormat.Markdown);
        Assert.Contains("1 additional active finding(s) omitted", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("21. [", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_summary_labels_its_score_as_a_projection_and_still_round_trips()
    {
        var report = CreateReport("review-projection", findingCount: 1);

        var canonical = QualityRunReportRenderer.Render(report, QualityReportFormat.Json);
        using var json = JsonDocument.Parse(canonical);
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(85, summary.GetProperty("aggregateScore").GetInt32());
        Assert.Equal("B", summary.GetProperty("aggregateBand").GetString());
        Assert.Equal(85, summary.GetProperty("score").GetInt32());
        Assert.Equal("B", summary.GetProperty("grade").GetString());

        var validation = RunReportSchema.Value.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());

        var roundTrip = QualityRunReportJson.Deserialize(canonical);
        Assert.Equal(85, roundTrip.Summary.AggregateScore);
        Assert.Equal("B", roundTrip.Summary.AggregateBand);

        var markdown = QualityRunReportRenderer.Render(report, QualityReportFormat.Markdown);
        Assert.Contains("Aggregate score (projection) 85/100 (B)", markdown, StringComparison.Ordinal);
        Assert.Contains("No unit carries it as its grade", markdown, StringComparison.Ordinal);
        Assert.Contains("<span>Aggregate score B</span>",
            QualityRunReportRenderer.Render(report, QualityReportFormat.Html), StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_evidence_and_operation_provenance_round_trip_in_native_json()
    {
        var report = CreateReport("review-native-evidence", findingCount: 1);
        var anchor = new FindingAnchor("primary", FindingAnchorRole.Primary, "src/App.cs",
            new FindingRange(new(3, 2), new(3, 9)),
            new CapturedExcerpt("unsafe()", "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64)), "App.Run");
        var related = anchor with { Id = "related", Role = FindingAnchorRole.Related };
        var evidence = new FindingEvidenceItem("span", FindingEvidenceClass.SourceSpan,
            FindingEvidenceStatus.Observed, "primary");
        var legacyClaim = new FindingEvidenceItem("legacy", FindingEvidenceClass.LegacyClaim,
            FindingEvidenceStatus.Unverified, Summary: "A retained claim, not a verified reproduction.");
        var reviewer = new ReviewerIdentity("codex", "gpt-6-astra", "0.154.0", "provider-observed-run",
            new ReviewerUsage("codex", 100, 25, 10, 5, 1200),
            [new ReviewerSensorReference("fixture", "1", "sha256:" + new string('c', 64))],
            "gpt-5.6-sol", "high");
        report = report with
        {
            Observations = [report.Observations[0] with
            {
                SourceRevision = "git:" + new string('d', 40),
                Reviewer = reviewer,
                Findings = [report.Observations[0].Findings[0] with
                {
                    Anchors = [anchor, related],
                    EvidenceItems = [evidence, legacyClaim],
                    Reproduction = new ReproductionInfo(ReproductionStatus.Unknown, "No command was executed."),
                }],
            }],
        };

        var canonical = QualityRunReportJson.Serialize(report);
        using var json = JsonDocument.Parse(canonical);
        var validation = RunReportSchema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
        var observation = Assert.Single(json.RootElement.GetProperty("observations").EnumerateArray());
        var finding = Assert.Single(observation.GetProperty("findings").EnumerateArray());
        Assert.Equal("sourceSpan", finding.GetProperty("evidenceItems")[0].GetProperty("class").GetString());
        Assert.Equal("unverified", finding.GetProperty("evidenceItems")[1].GetProperty("status").GetString());
        Assert.Equal("unknown", finding.GetProperty("reproduction").GetProperty("status").GetString());
        Assert.False(observation.GetProperty("reviewer").TryGetProperty("resolvedThinkingLevel", out _));
        Assert.False(observation.GetProperty("reviewer").TryGetProperty("executedThinkingLevel", out _));

        var roundTrip = QualityRunReportJson.Deserialize(canonical);
        var captured = Assert.Single(roundTrip.Observations);
        Assert.Equal(report.Run.Model, roundTrip.Run.Model);
        Assert.Equal(report.Observations[0].SourceRevision, captured.SourceRevision);
        Assert.Equal("gpt-6-astra", captured.Reviewer!.Model);
        Assert.Equal("gpt-5.6-sol", captured.Reviewer.RequestedModel);
        Assert.Equal("high", captured.Reviewer.RequestedThinkingLevel);
        Assert.Equal(reviewer.Usage, captured.Reviewer.Usage);
        Assert.Equal(reviewer.Sensors!.Single(), Assert.Single(captured.Reviewer.Sensors!));
        var retained = Assert.Single(captured.Findings);
        Assert.Equal(new[] { anchor, related }, retained.Anchors);
        Assert.Equal(new[] { evidence, legacyClaim }, retained.EvidenceItems);
        Assert.Equal(ReproductionStatus.Unknown, retained.Reproduction!.Status);
        Assert.Equal("No command was executed.", retained.Reproduction.Reason);
    }

    [Fact]
    public void Earlier_v1_reports_without_structured_fields_remain_readable_and_do_not_invent_evidence()
    {
        var canonical = QualityRunReportJson.Serialize(CreateReport("review-legacy-v1", findingCount: 1));
        using var json = JsonDocument.Parse(canonical);
        var observation = json.RootElement.GetProperty("observations")[0];
        var finding = observation.GetProperty("findings")[0];
        Assert.False(observation.TryGetProperty("reviewer", out _));
        Assert.False(observation.TryGetProperty("sourceRevision", out _));
        Assert.False(finding.TryGetProperty("anchors", out _));
        Assert.False(finding.TryGetProperty("evidenceItems", out _));
        Assert.False(finding.TryGetProperty("reproduction", out _));

        var roundTrip = QualityRunReportJson.Deserialize(canonical);
        Assert.Null(roundTrip.Observations[0].Reviewer);
        Assert.Null(roundTrip.Observations[0].SourceRevision);
        Assert.Null(roundTrip.Observations[0].Findings[0].Anchors);
        Assert.Null(roundTrip.Observations[0].Findings[0].EvidenceItems);
        Assert.Null(roundTrip.Observations[0].Findings[0].Reproduction);
        Assert.Equal("Hostile </style><script> evidence", roundTrip.Observations[0].Findings[0].Evidence);
        var validation = RunReportSchema.Value.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    [Fact]
    public void Store_replaces_atomically_and_ignores_incomplete_temporary_writes()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-store-").FullName;
        try
        {
            var store = new QualityRunReportStore(root);
            var first = CreateReport("review-atomic", findingCount: 1);
            store.Save(first);
            Directory.CreateDirectory(store.ReportsPath);
            File.WriteAllText(Path.Combine(store.ReportsPath, ".review-crash.123.tmp"), "{\"run\":");

            var second = first with
            {
                Run = first.Run with { Revision = 2 },
                Summary = first.Summary with { Score = 91, Grade = "A" },
            };
            store.Save(second);

            Assert.Equal(2, store.Load(first.Run.Id).Run.Revision);
            Assert.Equal(91, store.Load(first.Run.Id).Summary.Score);
            Assert.Single(store.LoadAll());
            Assert.DoesNotContain(Directory.EnumerateFiles(store.ReportsPath, "*.tmp", SearchOption.TopDirectoryOnly),
                path => !Path.GetFileName(path).StartsWith(".review-crash", StringComparison.Ordinal));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Cli_writes_run_artifact_before_returning_a_gate_failure()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-cli-").FullName;
        try
        {
            var report = CreateReport("review-cli", findingCount: 1);
            new QualityRunReportStore(root).Save(report);
            var output = Path.Combine(root, "artifacts", "quality-run.md");

            var exitCode = await global::QualityCli.RunAsync(
                ["report", root, "--run", report.Run.Id, "--format", "markdown", "--output", output,
                    "--fail-on", "high"]);

            Assert.Equal(1, exitCode);
            Assert.True(File.Exists(output));
            Assert.Contains("# Quality Studio review run", await File.ReadAllTextAsync(
                output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Equal(2, await global::QualityCli.RunAsync(
                ["report", root, "--run", "missing-run", "--format", "json"]));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    [Fact]
    public void Trend_filters_series_selects_highest_revision_and_pages_after_thirty_points()
    {
        var origin = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var reports = Enumerable.Range(0, 35).Select(index =>
        {
            var report = CreateReport($"review-{index:00}", findingCount: index % 3);
            return report with
            {
                Run = report.Run with { FinishedAt = origin.AddHours(index) },
            };
        }).ToList();
        reports.Add(reports[10] with
        {
            Run = reports[10].Run with { Revision = 2 },
            Summary = reports[10].Summary with { Score = 99, Grade = "A" },
        });
        reports.Add(CreateReport("other-scope", findingCount: 0) with
        {
            Run = CreateReport("other-scope", findingCount: 0).Run with { ScopeUnitId = "other-unit" },
        });
        reports.Add(CreateReport("partial-event", findingCount: 0, complete: false) with
        {
            Run = CreateReport("partial-event", findingCount: 0, complete: false).Run with
            {
                FinishedAt = origin.AddHours(36),
            },
        });

        var first = QualityRunTrendBuilder.Build(reports, "code", "unit-project", "project", limit: 30);
        var second = QualityRunTrendBuilder.Build(reports, "code", "unit-project", "project", first.NextCursor, 30);

        Assert.Equal(30, first.Points.Count);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(6, second.Points.Count);
        Assert.Null(second.NextCursor);
        Assert.Single(first.Points.Concat(second.Points), point => point.RunId == "review-10");
        Assert.Equal(2, Assert.Single(first.Points.Concat(second.Points), point => point.RunId == "review-10").Revision);
        var partial = Assert.Single(first.Points, point => point.RunId == "partial-event");
        Assert.False(partial.Comparable);
        Assert.Null(partial.Score);
        Assert.DoesNotContain(first.Points.Concat(second.Points), point => point.RunId == "other-scope");
    }

    private static QualityRunReportDocument CreateReport(
        string id,
        int findingCount,
        bool complete = true)
    {
        var findings = Enumerable.Range(0, findingCount).Select(index => new QualityRunFinding(
            $"finding-{index}",
            $"quality.rule.{index}",
            "correctness",
            index == 0 ? "high" : "medium",
            "open",
            index == 0 ? "<script>alert(\"x\")</script>" : $"Finding {index}",
            $"Description {index}",
            $"Recommendation {index}",
            index == 0 ? "Hostile </style><script> evidence" : null,
            "sha256:" + index.ToString("x64"),
            [new QualityFindingLocation("src/App.cs", index + 1, 1, index + 1, 8)],
            "agent",
            null,
            null)).ToArray();
        var bySeverity = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["critical"] = 0,
            ["high"] = findings.Count(finding => finding.Severity == "high"),
            ["medium"] = findings.Count(finding => finding.Severity == "medium"),
            ["low"] = 0,
            ["info"] = 0,
        };
        var run = new QualityRunIdentity(
            id, 1, "default", "Fixture repository", "code", "unit-project", "project", ".",
            complete ? "done" : "capped", complete ? "complete" : "partial",
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            "gpt-5.6-sol", "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget(
            "unit-file", "App.cs", "src/App.cs", "sha256:" + new string('a', 64));
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            run,
            new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]),
            new QualityRunExecution(
                complete ? 1 : 0, 0, 0, complete ? 0 : 1, 0, complete ? "done" : "skipped", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(complete ? null : 50, null, complete ? "not-configured" : "reached",
                    complete ? null : "Token cap reached."),
                null),
            [new QualityRunObservation(
                "unit-project", "project", ".", complete ? "done" : "skipped", complete,
                complete ? ".quality/reviews/projects/root.review-meta.code.json" : null,
                complete ? "sha256:" + new string('b', 64) : null,
                complete ? run.FinishedAt : null,
                complete ? "sha256:" + new string('c', 64) : null,
                complete ? "provider-run" : null,
                complete ? new QualityRunGrade(85, "B", "Fixture grade.") : null,
                complete ? "Fixture summary." : null,
                complete ? findings : [])],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                complete ? 85 : null,
                complete ? "B" : null,
                new QualityRunFindingCounts(complete ? findings.Length : 0, bySeverity,
                    new Dictionary<string, int> { ["open"] = complete ? findings.Length : 0 }),
                complete && findings.Length > 0 ? "high" : null,
                complete ? null : "Run ended in state capped."));
    }
}
