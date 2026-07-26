using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class QualityReportTests
{
    [Fact]
    public async Task Report_reconstructs_score_curve_from_committed_sidecar_generations()
    {
        using var fixture = await ReportRepositoryFixture.CreateAsync(60);
        await fixture.CommitScoreAsync(75);
        await fixture.CommitScoreAsync(92);

        var report = await new QualityReportBuilder(() =>
                new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero))
            .BuildAsync([fixture.Request], TestContext.Current.CancellationToken);

        var repository = Assert.Single(report.Repositories);
        Assert.Equal(92, repository.Scorecard.Score);
        Assert.Equal("A", repository.Scorecard.Grade);
        Assert.Equal(100, repository.Scorecard.Coverage.Percent);
        Assert.Equal(1, repository.Scorecard.Staleness.Fresh);
        Assert.Equal(2, repository.Scorecard.Staleness.Missing);
        Assert.Equal(1, repository.Scorecard.Findings.BySeverity["high"]);
        Assert.Equal(1, repository.Scorecard.Findings.ByState["open"]);
        var trend = Assert.Single(repository.Trend, series => series.Kind == "code");
        Assert.Equal([60, 75, 92], trend.Points.Select(point => point.Score));
        Assert.All(trend.Points, point => Assert.Equal(12, point.Commit.Length));
    }

    [Fact]
    public async Task Json_and_sarif_outputs_validate_and_sarif_preserves_finding_identity()
    {
        using var fixture = await ReportRepositoryFixture.CreateAsync(68);
        var report = await new QualityReportBuilder().BuildAsync(
            [fixture.Request], TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(QualityReportRenderer.Render(report, QualityReportFormat.Json));
        var reportSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "schemas", "quality-report.v1.schema.json")));
        var reportValidation = reportSchema.Evaluate(json.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(reportValidation.IsValid, reportValidation.ToString());

        using var sarif = JsonDocument.Parse(QualityReportRenderer.Render(report, QualityReportFormat.Sarif));
        var sarifSchema = JsonSchema.FromText(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "schemas", "sarif-2.1.0-output.schema.json")));
        var sarifValidation = sarifSchema.Evaluate(sarif.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(sarifValidation.IsValid, sarifValidation.ToString());
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var result = Assert.Single(Assert.Single(sarif.RootElement.GetProperty("runs").EnumerateArray())
            .GetProperty("results").EnumerateArray());
        Assert.Equal("quality.test", result.GetProperty("ruleId").GetString());
        Assert.Equal(fixture.Fingerprint,
            result.GetProperty("partialFingerprints").GetProperty("qualityStudioFingerprint/v1").GetString());
    }

    [Fact]
    public async Task Cli_exit_codes_cover_passing_failing_and_invalid_gates()
    {
        using var fixture = await ReportRepositoryFixture.CreateAsync(68);
        var output = Path.Combine(fixture.Root, "report.json");

        Assert.Equal(0, await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--format", "json", "--output", output, "--fail-under", "60"]));
        Assert.Equal(1, await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--format", "json", "--output", output, "--fail-under", "70"]));
        Assert.Equal(1, await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--format", "json", "--output", output, "--fail-on", "high"]));
        Assert.Equal(0, await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--format", "json", "--output", output, "--fail-on", "critical"]));
        Assert.Equal(2, await global::QualityCli.RunAsync(
            ["report", fixture.Root, "--fail-under", "101"]));
    }

    private static string FindRepositoryRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "QualityStudio.slnx")))
            current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class ReportRepositoryFixture : IDisposable
    {
        private readonly string sidecarPath;
        private readonly string contentHash;
        private readonly string reviewedHash;
        private int score;

        private ReportRepositoryFixture(
            string root,
            string sidecarPath,
            string contentHash,
            string reviewedHash,
            string fingerprint,
            int score)
        {
            Root = root;
            this.sidecarPath = sidecarPath;
            this.contentHash = contentHash;
            this.reviewedHash = reviewedHash;
            Fingerprint = fingerprint;
            this.score = score;
            Request = new QualityReportRepository(
                "fixture",
                "Fixture repository",
                root,
                ["code", "security", "performance"],
                [new QualityReportSensor("fixture-sensor", "1.0.0", true, true)]);
        }

        public string Root { get; }
        public string Fingerprint { get; }
        public QualityReportRepository Request { get; }

        public static async Task<ReportRepositoryFixture> CreateAsync(int score)
        {
            var root = Directory.CreateTempSubdirectory("quality-report-fixture-").FullName;
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, ".quality", "reviews", "files"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "App.cs"),
                "namespace Fixture; public sealed class App { }\n", TestContext.Current.CancellationToken);
            await RunGitAsync(root, "init", "--quiet");
            await RunGitAsync(root, "config", "user.email", "quality@example.test");
            await RunGitAsync(root, "config", "user.name", "Quality Fixture");
            var contentHash = await ReviewSubjectHasher.ComputeFileContentHashAsync(
                Path.Combine(root, "src", "App.cs"), TestContext.Current.CancellationToken);
            const string unitId = "qs-v1/generic/file/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var reviewedHash = ReviewSubjectHasher.ComputeManifestHash(unitId,
                [new SubjectInputHash("src/App.cs", "file", contentHash)]);
            var fingerprint = "sha256:" + new string('b', 64);
            var fixture = new ReportRepositoryFixture(
                root,
                Path.Combine(root, ".quality", "reviews", "files", "app.review-meta.code.json"),
                contentHash,
                reviewedHash,
                fingerprint,
                score);
            await fixture.WriteSidecarAsync();
            await RunGitAsync(root, "add", ".");
            await RunGitAsync(root, "commit", "--quiet", "-m", $"score {score}");
            return fixture;
        }

        public async Task CommitScoreAsync(int nextScore)
        {
            score = nextScore;
            await WriteSidecarAsync();
            await RunGitAsync(Root, "add", ".");
            await RunGitAsync(Root, "commit", "--quiet", "-m", $"score {nextScore}");
        }

        private async Task WriteSidecarAsync()
        {
            const string unitId = "qs-v1/generic/file/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var metadata = new JsonObject
            {
                ["unit"] = new JsonObject
                {
                    ["id"] = unitId,
                    ["level"] = "file",
                    ["path"] = "src/App.cs",
                },
                ["reviewedAt"] = "2026-07-25T09:00:00.000Z",
                ["kind"] = "code",
                ["reviewedHash"] = new JsonObject { ["value"] = reviewedHash },
                ["subjectInputs"] = new JsonArray(new JsonObject
                {
                    ["path"] = "src/App.cs",
                    ["selector"] = "file",
                    ["contentHash"] = contentHash,
                }),
                ["grade"] = new JsonObject
                {
                    ["score"] = score,
                    ["band"] = QualityReportBuilder.Grade(score),
                    ["rationale"] = "Fixture score.",
                },
                ["findings"] = new JsonArray(new JsonObject
                {
                    ["id"] = "finding-" + new string('b', 64),
                    ["ruleId"] = "quality.test",
                    ["fingerprint"] = Fingerprint,
                    ["severity"] = "high",
                    ["title"] = "Fixture finding",
                    ["description"] = "A deterministic fixture finding.",
                    ["recommendation"] = "Fix the fixture.",
                    ["locations"] = new JsonArray(new JsonObject
                    {
                        ["path"] = "src/App.cs",
                        ["range"] = new JsonObject
                        {
                            ["start"] = new JsonObject { ["line"] = 1, ["column"] = 1 },
                            ["end"] = new JsonObject { ["line"] = 1, ["column"] = 10 },
                        },
                    }),
                }),
            };
            await File.WriteAllTextAsync(sidecarPath, metadata.ToJsonString(),
                TestContext.Current.CancellationToken);
        }

        private static async Task RunGitAsync(string root, params string[] arguments)
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, error);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
