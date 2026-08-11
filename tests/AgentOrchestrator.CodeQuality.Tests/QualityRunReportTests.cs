using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

[Collection("Quality report schema validation")]
public sealed class QualityRunReportTests
{
    [Fact]
    public void Json_and_sarif_validate_and_html_is_self_contained_and_escaped()
    {
        var report = Report("review-one", "done", "complete", 87, "<script>alert('x')</script>");

        using var json = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Json));
        var reportSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-run-report.v1.schema.json")));
        var reportValidation = reportSchema.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(reportValidation.IsValid, reportValidation.ToString());

        foreach (var terminalState in new[] { "capped", "failed", "cancelled" })
        {
            var partial = Report($"review-{terminalState}", terminalState, "partial", null, terminalState) with
            {
                Run = Report($"review-{terminalState}", terminalState, "partial", null, terminalState).Run with
                    { PartialReason = $"Run was {terminalState}." },
                Execution = Report($"review-{terminalState}", terminalState, "partial", null, terminalState).Execution with
                    { Outcome = terminalState },
                Delta = new QualityRunDelta("unavailable", null, [], [], [], []),
            };
            using var partialJson = JsonDocument.Parse(QualityRunReportRenderer.Render(partial, QualityReportFormat.Json));
            var partialValidation = reportSchema.Evaluate(partialJson.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            Assert.True(partialValidation.IsValid, $"{terminalState}: {partialValidation}");
        }

        var reused = Report("review-fresh", "done", "complete", 87, "Fresh") with
        {
            Execution = Report("review-fresh", "done", "complete", 87, "Fresh").Execution with
                { Reviewed = 0, ReusedFresh = 1, UsageOperations = 0 },
            Observations = [Report("review-fresh", "done", "complete", 87, "Fresh").Observations[0] with
                { Outcome = "reused-fresh", ProducedByRun = false }],
        };
        using var reusedJson = JsonDocument.Parse(QualityRunReportRenderer.Render(reused, QualityReportFormat.Json));
        var reusedValidation = reportSchema.Evaluate(reusedJson.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(reusedValidation.IsValid, reusedValidation.ToString());
        Assert.Equal(0, reused.Execution.Reviewed);
        Assert.Equal(1, reused.Execution.ReusedFresh);
        Assert.All(reused.Observations, observation => Assert.False(observation.ProducedByRun));

        using var sarif = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Sarif));
        var sarifSchemaJson = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "sarif-2.1.0-output.schema.json")))!.AsObject();
        // JsonSchema.Net registers absolute $id values globally and rejects loading the same
        // schema twice in one test process. The repository-report suite already owns that ID.
        sarifSchemaJson.Remove("$id");
        var sarifSchema = JsonSchema.FromText(sarifSchemaJson.ToJsonString());
        var sarifValidation = sarifSchema.Evaluate(sarif.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(sarifValidation.IsValid, sarifValidation.ToString());
        var result = Assert.Single(Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray())
            .GetProperty("results").EnumerateArray());
        Assert.Equal(Fingerprint,
            result.GetProperty("partialFingerprints").GetProperty("primaryLocationLineHash").GetString());
        Assert.Equal("new", result.GetProperty("baselineState").GetString());

        var html = QualityRunReportRenderer.Render(report, QualityReportFormat.Html);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetTempPath(), html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Markdown_is_bounded_and_cli_writes_artifact_before_failing_gate()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-report-").FullName;
        try
        {
            var findings = Enumerable.Range(1, 23).Select(index => Finding(
                "sha256:" + index.ToString("x64"), $"Finding {index}")).ToArray();
            var report = Report("review-bounded", "done", "complete", 87, "Finding 1") with
            {
                Observations = [Report("review-bounded", "done", "complete", 87, "Finding 1").Observations[0] with
                {
                    Findings = findings,
                }],
                Summary = new QualityRunSummary(87, "B", findings.Length,
                    new Dictionary<string, int> { ["critical"] = 0, ["high"] = findings.Length, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                    new Dictionary<string, int> { ["open"] = findings.Length }, "high", null),
            };
            QualityRunReportStore.Save(root, report);
            var markdown = QualityRunReportRenderer.Render(report, QualityReportFormat.Markdown);
            Assert.Contains("3 additional active finding(s) omitted", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("23. [", markdown, StringComparison.Ordinal);

            var output = Path.Combine(root, "run.md");
            var exit = await global::QualityCli.RunAsync(
                ["report", root, "--run", report.Run.Id, "--format", "markdown", "--output", output, "--fail-on", "high"]);
            Assert.Equal(1, exit);
            Assert.True(File.Exists(output));
            Assert.Contains("Quality Studio review run",
                await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Store_is_atomic_and_trend_filters_series_and_keeps_partial_events()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-trend-").FullName;
        try
        {
            var complete = Report("review-complete", "done", "complete", 87, "Complete");
            var partial = Report("review-capped", "capped", "partial", null, "Partial") with
            {
                Run = Report("review-capped", "capped", "partial", null, "Partial").Run with
                {
                    FinishedAt = new DateTimeOffset(2026, 8, 11, 10, 1, 0, TimeSpan.Zero),
                    PartialReason = "Token cap reached.",
                },
                Summary = new QualityRunSummary(null, null, 1,
                    new Dictionary<string, int> { ["high"] = 1 },
                    new Dictionary<string, int> { ["open"] = 1 }, "high", "Token cap reached."),
            };
            var otherScope = Report("review-other", "done", "complete", 90, "Other") with
            {
                Run = Report("review-other", "done", "complete", 90, "Other").Run with
                {
                    Scope = new QualityRunScope("other-unit", "file", "Other.cs"),
                },
            };
            QualityRunReportStore.Save(root, complete);
            var storedBytes = QualityRunReportRenderer.Render(
                QualityRunReportStore.Load(root, complete.Run.Id), QualityReportFormat.Json);
            var sidecar = Path.Combine(root, complete.Observations[0].SidecarPath!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            File.WriteAllText(sidecar, "later sidecar generation");
            Assert.Equal(storedBytes, QualityRunReportRenderer.Render(
                QualityRunReportStore.Load(root, complete.Run.Id), QualityReportFormat.Json));
            QualityRunReportStore.Save(root, partial);
            QualityRunReportStore.Save(root, otherScope);
            File.WriteAllText(Path.Combine(root, QualityRunReportStore.RelativeReportsPath.Replace('/', Path.DirectorySeparatorChar),
                "incomplete.tmp"), "{");

            var trend = QualityRunTrendBuilder.Build(root, complete, page: 1, pageSize: 1);
            Assert.Equal(2, trend.Total);
            Assert.Single(trend.Points);
            Assert.False(trend.Points[0].ConnectScore);
            Assert.Equal("capped", trend.Points[0].State);
            Assert.DoesNotContain(Directory.EnumerateFiles(
                    Path.GetDirectoryName(QualityRunReportStore.ReportPath(root, complete.Run.Id))!, "*.tmp", SearchOption.TopDirectoryOnly),
                path => !path.EndsWith("incomplete.tmp", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static QualityRunReportDocument Report(
        string id, string state, string completeness, int? score, string title)
    {
        var target = new QualityRunTarget("unit-one", "src/App.cs", Hash);
        var finding = Finding(Fingerprint, title);
        var observation = new QualityRunObservation(
            target.UnitId, target.Path, "file", "reviewed", true,
            ".quality/reviews/files/app.review-meta.code.json", Hash, Hash, "provider-run", At,
            new ReviewGrade(score ?? 87, GradeBand.B, "Captured grade."), "Captured summary.", [finding], []);
        return new QualityRunReportDocument(
            QualityRunReportStore.SchemaId, 1, 1,
            new QualityRunIdentity(id, "default", "Fixture", "code",
                new QualityRunScope("unit-one", "file", "src/App.cs"), state, completeness,
                At.AddMinutes(-1), At.AddSeconds(-30), At, "test-model", "high", "test-cli", false,
                completeness == "complete" ? null : "Partial run."),
            new QualityRunSubject([target], QualityRunReportFingerprint.Manifest([target])),
            new QualityRunExecution(state, 1, 0, 0, 0, 0, 1,
                new TokenUsage(10, 5, 0, 0, 100), 0.01m, "USD", "priced", null, null, []),
            [observation],
            new QualityRunDelta("available", "review-prior", [Fingerprint], [], [], []),
            new QualityRunSummary(score, score.HasValue ? "B" : null, 1,
                new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                new Dictionary<string, int> { ["open"] = 1, ["accepted"] = 0, ["waived"] = 0, ["false-positive"] = 0, ["resolved"] = 0 },
                "high", completeness == "complete" ? null : "Partial run."));
    }

    private static QualityFinding Finding(string fingerprint, string title) => new(
        "default", "finding-one", "quality.test", "code", "high", "open", title,
        "Finding description.", "Apply the fix.", fingerprint,
        [new QualityFindingLocation("src/App.cs", 1, 1, 1, 5)]);

    private static readonly DateTimeOffset At = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fingerprint = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
}
