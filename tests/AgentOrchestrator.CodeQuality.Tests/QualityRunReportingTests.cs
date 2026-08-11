using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityRunReportingTests
{
    [Fact]
    public void Canonical_snapshot_validates_and_survives_later_sidecar_overwrite()
    {
        using var fixture = new RunReportFixture();
        var report = fixture.Build("run-one", "done", 82, 1);
        var store = new QualityRunReportStore(fixture.Root);
        var path = store.Write(report);
        var before = File.ReadAllBytes(path);

        File.WriteAllText(fixture.SidecarPath, "{\"later\":true}");
        var loaded = store.Load("run-one");

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(82, loaded.Summary.Score);
        Assert.Equal("sha256:" + new string('a', 64), Assert.Single(loaded.Observations).ReviewedHash);
        using var json = JsonDocument.Parse(QualityRunReportJson.Serialize(loaded));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-run-report.v1.schema.json")),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var validation = schema.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
    }

    [Fact]
    public void Atomic_store_ignores_incomplete_temporary_write()
    {
        using var fixture = new RunReportFixture();
        var store = new QualityRunReportStore(fixture.Root);
        store.Write(fixture.Build("run-one", "done", 90, 0));
        File.WriteAllText(Path.Combine(store.ReportsPath, "run-two.incomplete.tmp"), "{");

        var reports = store.LoadAll();

        Assert.Single(reports);
        Assert.Equal("run-one", reports[0].Run.Id);
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void Partial_terminal_states_validate_without_inventing_a_score(string state)
    {
        using var fixture = new RunReportFixture();
        var report = fixture.Build($"run-{state}", state, 90, 1);
        var serialized = QualityRunReportJson.Serialize(report);
        using var json = JsonDocument.Parse(serialized);
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "quality-run-report.v1.schema.json")),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        var validation = schema.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(validation.IsValid, validation.ToString());
        Assert.Equal("partial", report.Run.Completeness);
        Assert.Null(report.Summary.Score);
        Assert.Null(report.Summary.Grade);
        Assert.DoesNotContain(fixture.Root, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderers_are_bounded_offline_safe_and_sarif_interoperable()
    {
        using var fixture = new RunReportFixture();
        var report = fixture.Build("run-hostile", "done", 75, 21, "<script>alert('root')</script>");

        var markdown = QualityRunReportRenderer.Render(report, QualityReportFormat.Markdown);
        Assert.Contains("1 additional active finding(s)", markdown, StringComparison.Ordinal);
        Assert.Equal(20, markdown.Split('\n').Count(line => line.StartsWith("1. [", StringComparison.Ordinal) ||
                                                            line.StartsWith("2. [", StringComparison.Ordinal) ||
                                                            (line.Length > 2 && char.IsDigit(line[0]) && line.Contains(". [", StringComparison.Ordinal))));

        var html = QualityRunReportRenderer.Render(report, QualityReportFormat.Html);
        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, html, StringComparison.Ordinal);

        using var sarif = JsonDocument.Parse(QualityRunReportRenderer.Render(report, QualityReportFormat.Sarif));
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "schemas", "sarif-2.1.0-output.schema.json")),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var validation = schema.Evaluate(sarif.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(validation.IsValid, validation.ToString());
        var run = Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray());
        Assert.StartsWith("quality-studio/fixture/code/", run.GetProperty("automationDetails").GetProperty("id").GetString(), StringComparison.Ordinal);
        var result = Assert.Single(run.GetProperty("results").EnumerateArray().Take(1));
        Assert.Equal(result.GetProperty("partialFingerprints").GetProperty("qualityStudioFingerprint/v1").GetString(),
            result.GetProperty("partialFingerprints").GetProperty("primaryLocationLineHash").GetString());
        Assert.False(result.TryGetProperty("baselineState", out _));
    }

    [Fact]
    public void Comparable_delta_and_trend_use_same_series_and_highest_revision()
    {
        using var fixture = new RunReportFixture();
        var first = fixture.Build("run-one", "done", 80, 1);
        var second = fixture.Build("run-two", "done", 90, 2, existing: [first]);
        var partialRevisionOne = fixture.Build("run-resume", "capped", 0, 1, revision: 1);
        var resumedRevisionTwo = fixture.Build("run-resume", "done", 92, 1, revision: 2, existing: [first, second]);
        var otherScope = fixture.Build("other-scope", "done", 99, 0,
            scope: new QualityRunScope("other", "file", "Other.cs", "Other"));

        Assert.Equal("available", second.Delta.Status);
        Assert.Equal("run-one", second.Delta.PreviousRunId);
        Assert.Single(second.Delta.Persisting);
        Assert.Single(second.Delta.New);
        var trend = QualityRunTrendBuilder.Build(
            [first, second, partialRevisionOne, resumedRevisionTwo, otherScope],
            "fixture", "code", "unit:file", "file", pageSize: 2);

        Assert.Equal(3, trend.Total);
        Assert.Equal(2, trend.Points.Count);
        Assert.Contains(trend.Points, point => point.RunId == "run-resume" && point.Revision == 2);
        Assert.DoesNotContain(trend.Points, point => point.RunId == "run-resume" && point.Revision == 1);
    }

    [Fact]
    public void Trend_pages_beyond_the_default_thirty_runs()
    {
        using var fixture = new RunReportFixture();
        var reports = Enumerable.Range(1, 35)
            .Select(index => fixture.Build($"run-{index:00}", "done", 70 + index % 20, index % 3,
                revision: index))
            .ToArray();

        var secondPage = QualityRunTrendBuilder.Build(
            reports, "fixture", "code", "unit:file", "file", page: 2, pageSize: 30);

        Assert.Equal(35, secondPage.Total);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(5, secondPage.Points.Count);
    }

    [Fact]
    public async Task Cli_writes_run_artifact_before_returning_gate_failure()
    {
        using var fixture = new RunReportFixture();
        new QualityRunReportStore(fixture.Root).Write(fixture.Build("run-gate", "done", 70, 1));
        var output = Path.Combine(fixture.Root, "artifacts", "run.md");

        var exitCode = await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--run", "run-gate", "--format", "markdown", "--output", output,
                "--fail-under", "80"]);

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(output));
        Assert.Contains("run-gate", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private sealed class RunReportFixture : IDisposable
    {
        public RunReportFixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-run-report-").FullName;
            SidecarPath = Path.Combine(Root, ".quality", "reviews", "sample.review-meta.code.json");
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
        }

        public string Root { get; }
        public string SidecarPath { get; }

        public QualityRunReportDocument Build(
            string runId,
            string state,
            int score,
            int findings,
            string? hostileTitle = null,
            int revision = 1,
            IReadOnlyList<QualityRunReportDocument>? existing = null,
            QualityRunScope? scope = null)
        {
            var findingNodes = new JsonArray(Enumerable.Range(0, findings).Select(index => (JsonNode)new JsonObject
            {
                ["id"] = $"finding-{index}",
                ["ruleId"] = $"rule-{index}",
                ["aspect"] = "correctness",
                ["severity"] = index == 0 ? "high" : "medium",
                ["state"] = "open",
                ["title"] = index == 0 && hostileTitle is not null ? hostileTitle : $"Finding {index}",
                ["description"] = $"Description {index}",
                ["recommendation"] = $"Fix {index}",
                ["fingerprint"] = "sha256:" + index.ToString("x64"),
                ["locations"] = new JsonArray(new JsonObject
                {
                    ["path"] = "Sample.cs",
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = index + 1, ["column"] = 1 },
                        ["end"] = new JsonObject { ["line"] = index + 1, ["column"] = 2 },
                    },
                }),
            }).ToArray());
            var sidecar = new JsonObject
            {
                ["$schema"] = "https://agent-orchestrator.dev/quality/schemas/review-meta.v2.schema.json",
                ["schemaVersion"] = 2,
                ["unit"] = new JsonObject { ["id"] = "unit:file", ["adapter"] = "dotnet", ["level"] = "file", ["path"] = "Sample.cs", ["displayName"] = "Sample.cs" },
                ["reviewedAt"] = "2026-08-11T07:00:00Z",
                ["kind"] = "code",
                ["reviewer"] = new JsonObject { ["agent"] = "test", ["model"] = "test-model", ["runId"] = "provider-operation" },
                ["reviewedHash"] = new JsonObject { ["algorithm"] = "sha256", ["canonicalization"] = "quality-studio-subject-manifest-v1", ["value"] = "sha256:" + new string('a', 64) },
                ["grade"] = new JsonObject { ["score"] = score, ["band"] = QualityReportBuilder.Grade(score), ["rationale"] = "Measured scope" },
                ["summary"] = "Captured summary",
                ["findings"] = findingNodes,
                ["deterministicEvidence"] = new JsonArray(),
            }.ToJsonString();
            File.WriteAllText(SidecarPath, sidecar);
            var operationState = state == "done" ? "done" : state == "cancelled" ? "cancelled" : "skipped";
            var snapshot = state == "done" ? new ReviewObservationSnapshot(SidecarPath, sidecar, sidecar) : null;
            var at = new DateTimeOffset(2026, 8, 11, 7, Math.Min(59, revision), 0, TimeSpan.Zero);
            return QualityRunReportBuilder.Build(Root, new QualityRunReportBuildInput(
                runId, revision, "fixture", "Fixture", "code", scope ?? new QualityRunScope("unit:file", "file", "Sample.cs", "Sample.cs"),
                state, at.AddMinutes(-1), at.AddSeconds(-30), at, "test-model", "test-cli", false,
                [new QualityRunTarget("unit:file", "Sample.cs", "subject-hash")],
                [new QualityRunOperationInput("Sample.cs", "file", operationState, false, snapshot)], null,
                state == "done" ? [] : [$"Run stopped at {Root}"], 1, new TokenUsage(10, 5, 0, 0, 100),
                null, null, 0.01m, "USD", "known",
                state == "done" ? null : $"Partial run stopped at {Root}"), existing);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
