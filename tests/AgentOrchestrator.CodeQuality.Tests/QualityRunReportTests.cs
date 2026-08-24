using System.Net;
using System.Text.Json;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunReportTests
{
    // JsonSchema.Net registers a schema globally by its $id, so the document is built once per
    // assembly instead of once per test. Lazy keeps a fixture-path failure inside the test that needs
    // the schema instead of turning it into a TypeInitializationException for the whole class.
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
    public void Projections_pin_the_reviewed_commit_and_state_the_full_token_ledger()
    {
        var capped = CreateReport("review-pinned", findingCount: 1, complete: false);
        var pinned = capped with
        {
            Run = capped.Run with
            {
                SourceRevision = new QualityRunSourceRevision(
                    new string('a', 40),
                    new string('a', 12),
                    "feature/<script>",
                    true,
                    new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero)),
            },
            Execution = capped.Execution with
            {
                Usage = capped.Execution.Usage with
                {
                    InputEstimateDeviationPercent = 12.5m,
                    OutputEstimateDeviationPercent = -4m,
                },
            },
        };

        using var canonical = JsonDocument.Parse(QualityRunReportRenderer.Render(pinned, QualityReportFormat.Json));
        var validation = RunReportSchema.Value.Evaluate(canonical.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal(new string('a', 40), canonical.RootElement.GetProperty("run")
            .GetProperty("sourceRevision").GetProperty("commitSha").GetString());

        var html = QualityRunReportRenderer.Render(pinned, QualityReportFormat.Html);
        Assert.Contains(new string('a', 12), html, StringComparison.Ordinal);
        Assert.Contains("uncommitted changes", html, StringComparison.Ordinal);
        Assert.Contains(">default<", html, StringComparison.Ordinal);
        Assert.Contains("Token ledger and cap", html, StringComparison.Ordinal);
        // Every dynamic ledger value is HTML-encoded, so the separator arrives as a character entity.
        Assert.Contains(WebUtility.HtmlEncode("reached · limit 50 tokens · Token cap reached."), html,
            StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("input +12.5% · output -4%"), html, StringComparison.Ordinal);
        Assert.Contains("2026-08-11 08:02:00 UTC", html, StringComparison.Ordinal);
        Assert.DoesNotContain("feature/<script>", html, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode("feature/<script>"), html, StringComparison.Ordinal);

        var markdown = QualityRunReportRenderer.Render(pinned, QualityReportFormat.Markdown);
        Assert.Contains($"Repository `default` at commit {new string('a', 12)} (", markdown, StringComparison.Ordinal);
        Assert.Contains(") · uncommitted changes", markdown, StringComparison.Ordinal);
        Assert.Contains("- Cap: reached · limit 50 tokens · Token cap reached.", markdown, StringComparison.Ordinal);

        using var sarif = JsonDocument.Parse(QualityRunReportRenderer.Render(pinned, QualityReportFormat.Sarif));
        var properties = Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray()).GetProperty("properties");
        Assert.Equal(new string('a', 40), properties.GetProperty("sourceRevisionId").GetString());
        Assert.True(properties.GetProperty("sourceDirty").GetBoolean());
    }

    [Fact]
    public void Snapshots_without_a_captured_commit_stay_valid_and_say_so()
    {
        var report = CreateReport("review-unpinned", findingCount: 1);
        Assert.Null(report.Run.SourceRevision);

        using var canonical = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Json));
        var validation = RunReportSchema.Value.Evaluate(
            canonical.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
        Assert.False(canonical.RootElement.GetProperty("run").TryGetProperty("sourceRevision", out _));

        // Anchored on the rendered Commit cell: "unavailable" alone also appears in the token ledger.
        Assert.Contains("<dt>Commit</dt><dd><span class=\"muted\">unavailable</span></dd>",
            QualityRunReportRenderer.Render(report, QualityReportFormat.Html), StringComparison.Ordinal);
        Assert.Contains("<th>Commit</th><td><span class=\"muted\">unavailable</span></td>",
            QualityRunReportRenderer.Render(report, QualityReportFormat.Html), StringComparison.Ordinal);
        Assert.Contains("at commit unavailable", QualityRunReportRenderer.Render(report, QualityReportFormat.Markdown),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Html_separates_active_findings_from_suppressed_ones_it_lists()
    {
        var report = CreateReport("review-suppressed", findingCount: 2);
        var suppressed = report with
        {
            Observations = [report.Observations[0] with
            {
                Findings = report.Observations[0].Findings
                    .Select((finding, index) => index == 0 ? finding with { State = "waived" } : finding).ToArray(),
            }],
            Summary = report.Summary with { Findings = report.Summary.Findings with { Total = 1 } },
        };

        var html = QualityRunReportRenderer.Render(suppressed, QualityReportFormat.Html);
        Assert.Contains("1 active and 1 suppressed finding(s) are listed below.", html, StringComparison.Ordinal);
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
            TestDirectory.Delete(root);
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
            TestDirectory.Delete(root);
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
