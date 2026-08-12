using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using CodingAgentRunner.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class ReviewRunStoreTests
{
    [Fact]
    public async Task Server_stops_a_direct_api_run_at_its_token_cap_and_reports_skipped_units()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        var fake = new CappedExecutorFactory();
        try
        {
            await using var application = fixture.CreateApplication(fake);
            using var client = application.CreateClient();
            using var response = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
                model = "claude-sonnet-5",
                thinkingLevel = "high",
                tokenCap = 5,
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("test-agent", accepted.GetProperty("cliType").GetString());
            Assert.Equal("claude-sonnet-5", accepted.GetProperty("model").GetString());
            Assert.Equal("high", accepted.GetProperty("thinkingLevel").GetString());
            Assert.True(accepted.GetProperty("estimate").GetProperty("inputTokens").GetInt64() > 0);
            Assert.True(accepted.GetProperty("estimate").GetProperty("cost").GetDecimal() > 0);

            var run = await WaitForStateAsync(client, accepted.GetProperty("id").GetString()!, "capped", cancellationToken);

            Assert.Equal(1, run.GetProperty("completedFiles").GetInt32());
            Assert.Equal(1, run.GetProperty("skippedFiles").GetInt32());
            Assert.Equal("skipped", run.GetProperty("aggregateState").GetString());
            Assert.Contains(run.GetProperty("files").EnumerateArray(), file => file.GetProperty("state").GetString() == "done");
            Assert.Contains(run.GetProperty("files").EnumerateArray(), file => file.GetProperty("state").GetString() == "skipped");
            Assert.Contains("Token cap", run.GetProperty("stopReason").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, fake.OperationCount);
            var reportStore = new QualityRunReportStore(fixture.RepositoryRoot);
            var cappedReport = reportStore.Load(accepted.GetProperty("id").GetString()!);
            Assert.Equal(1, cappedReport.Run.Revision);
            Assert.Equal("capped", cappedReport.Run.State);
            Assert.Equal("partial", cappedReport.Run.Completeness);

            using var resume = await client.PostAsJsonAsync(
                $"/api/review/runs/{accepted.GetProperty("id").GetString()}/resume",
                new { tokenCap = 100 }, cancellationToken);
            resume.EnsureSuccessStatusCode();
            var completed = await WaitForStateAsync(client, accepted.GetProperty("id").GetString()!, "done", cancellationToken);
            Assert.Equal(2, completed.GetProperty("completedFiles").GetInt32());
            Assert.Equal(0, completed.GetProperty("skippedFiles").GetInt32());
            Assert.Equal("done", completed.GetProperty("aggregateState").GetString());
            Assert.Equal(3, completed.GetProperty("usageOperations").GetInt32());
            Assert.True(completed.GetProperty("deviation").GetProperty("inputTokensPercent").GetDecimal() < 0);
            Assert.Equal(3, fake.OperationCount);
            Assert.Equal("test-agent", fake.CliType);
            Assert.Equal("claude-sonnet-5", fake.Model);
            Assert.Equal("high", fake.ThinkingLevel);
            var completedReport = reportStore.Load(accepted.GetProperty("id").GetString()!);
            Assert.Equal(2, completedReport.Run.Revision);
            Assert.Equal("complete", completedReport.Run.Completeness);
            Assert.Equal(3, completedReport.Observations.Count);
            Assert.Equal(3, completedReport.Execution.Reviewed);
            Assert.All(completedReport.Observations, observation => Assert.True(observation.ProducedByRun));
            Assert.DoesNotContain(fixture.RepositoryRoot, QualityRunReportJson.Serialize(completedReport),
                StringComparison.OrdinalIgnoreCase);
            var canonicalBeforeOverwrite = await File.ReadAllBytesAsync(
                reportStore.PathFor(completedReport.Run.Id), cancellationToken);
            var mutableSidecar = Path.Combine(fixture.RepositoryRoot,
                completedReport.Observations[0].SidecarPath!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(mutableSidecar)!);
            await File.WriteAllTextAsync(mutableSidecar, "{\"laterSweep\":true}", cancellationToken);
            Assert.Equal(canonicalBeforeOverwrite, await File.ReadAllBytesAsync(
                reportStore.PathFor(completedReport.Run.Id), cancellationToken));

            using var jsonResponse = await client.GetAsync(
                $"/api/review/runs/{completedReport.Run.Id}/report?format=json", cancellationToken);
            jsonResponse.EnsureSuccessStatusCode();
            Assert.Equal("application/json", jsonResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains($"quality-run-{completedReport.Run.Id}.json",
                jsonResponse.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
            var exported = await jsonResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(completedReport.Run.Id, exported.GetProperty("run").GetProperty("id").GetString());
            Assert.Equal(3, exported.GetProperty("observations").GetArrayLength());

            using var sarifResponse = await client.GetAsync(
                $"/api/review/runs/{completedReport.Run.Id}/report?format=sarif", cancellationToken);
            sarifResponse.EnsureSuccessStatusCode();
            Assert.Equal("application/sarif+json", sarifResponse.Content.Headers.ContentType?.MediaType);
            var sarif = await sarifResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(completedReport.Run.Id,
                Assert.Single(sarif.GetProperty("runs").EnumerateArray()).GetProperty("properties")
                    .GetProperty("reviewRunId").GetString());

            var trendUrl = "/api/review/runs/trend?kind=code&scopeUnitId=" +
                           Uri.EscapeDataString(completedReport.Run.ScopeUnitId) + "&level=project";
            var trend = await client.GetFromJsonAsync<JsonElement>(trendUrl, cancellationToken);
            Assert.Contains(trend.GetProperty("points").EnumerateArray(), point =>
                point.GetProperty("runId").GetString() == completedReport.Run.Id &&
                point.GetProperty("comparable").GetBoolean());

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                fixture.Store.RunsPath, accepted.GetProperty("id").GetString()!, "result.json"), cancellationToken));
            Assert.Equal("claude-sonnet-5", result.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", result.RootElement.GetProperty("thinkingLevel").GetString());
            Assert.Equal("test-agent", result.RootElement.GetProperty("cli").GetString());
            Assert.Equal("done", result.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Server_reports_fresh_file_and_aggregate_skips_and_force_bypasses_them()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        var fake = new FreshnessExecutorFactory();
        try
        {
            await using var application = fixture.CreateApplication(fake);
            using var client = application.CreateClient();

            using var freshResponse = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
                model = "claude-sonnet-5",
            }, cancellationToken);
            freshResponse.EnsureSuccessStatusCode();
            var freshAccepted = await freshResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var fresh = await WaitForStateAsync(
                client, freshAccepted.GetProperty("id").GetString()!, "done", cancellationToken);

            Assert.Equal(2, fresh.GetProperty("completedFiles").GetInt32());
            Assert.Equal(2, fresh.GetProperty("skippedFiles").GetInt32());
            Assert.All(fresh.GetProperty("files").EnumerateArray(),
                file => Assert.Equal("skipped-fresh", file.GetProperty("state").GetString()));
            Assert.Equal("skipped-fresh", fresh.GetProperty("aggregateState").GetString());
            Assert.Equal(0, fresh.GetProperty("usageOperations").GetInt32());
            Assert.Equal(0, fake.AgentCalls);
            var freshReport = new QualityRunReportStore(fixture.RepositoryRoot)
                .Load(freshAccepted.GetProperty("id").GetString()!);
            Assert.Equal("complete", freshReport.Run.Completeness);
            Assert.Equal(0, freshReport.Execution.Reviewed);
            Assert.Equal(3, freshReport.Execution.ReusedFresh);
            Assert.All(freshReport.Observations, observation => Assert.False(observation.ProducedByRun));

            using var forcedResponse = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
                model = "claude-sonnet-5",
                force = true,
            }, cancellationToken);
            forcedResponse.EnsureSuccessStatusCode();
            var forcedAccepted = await forcedResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var forced = await WaitForStateAsync(
                client, forcedAccepted.GetProperty("id").GetString()!, "done", cancellationToken);

            Assert.Equal(0, forced.GetProperty("skippedFiles").GetInt32());
            Assert.All(forced.GetProperty("files").EnumerateArray(),
                file => Assert.Equal("done", file.GetProperty("state").GetString()));
            Assert.Equal("done", forced.GetProperty("aggregateState").GetString());
            Assert.Equal(3, fake.AgentCalls);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Non_terminal_run_resumes_after_restart_without_repeating_done_files()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("resume", "queued");
            fixture.Store.AppendProgress(new ReviewRunFileTransition(
                "Sample.cs", "done", stored.Manifest.CreatedAt, DateTimeOffset.UtcNow, stored.Manifest.RunId, null));
            fixture.Store.WriteStatus(stored.Status with
            {
                State = "running",
                CompletedFiles = 1,
                Cursor = 1,
                StartedAt = stored.Manifest.CreatedAt,
            });
            var progressPath = fixture.ProgressPath(stored.Manifest.RunId);
            var transitionsBefore = File.ReadAllLines(progressPath).Length;

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var run = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);

            Assert.Equal(1, run.GetProperty("completedFiles").GetInt32());
            Assert.Equal(0, run.GetProperty("failedFiles").GetInt32());
            Assert.Equal("done", Assert.Single(run.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
            Assert.Equal(transitionsBefore, File.ReadAllLines(progressPath).Length);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Skipped_fresh_file_is_durable_and_is_not_repeated_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("skipped", "queued");
            fixture.Store.AppendProgress(new ReviewRunFileTransition(
                "Sample.cs", "skipped-fresh", stored.Manifest.CreatedAt, DateTimeOffset.UtcNow,
                stored.Manifest.RunId, null));
            fixture.Store.WriteStatus(stored.Status with
            {
                State = "running",
                CompletedFiles = 1,
                SkippedFiles = 1,
                Cursor = 1,
                StartedAt = stored.Manifest.CreatedAt,
            });
            var progressPath = fixture.ProgressPath(stored.Manifest.RunId);
            var transitionsBefore = File.ReadAllLines(progressPath).Length;

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var run = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);

            Assert.Equal(1, run.GetProperty("completedFiles").GetInt32());
            Assert.Equal(1, run.GetProperty("skippedFiles").GetInt32());
            Assert.Equal("skipped-fresh",
                Assert.Single(run.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
            Assert.Equal(transitionsBefore, File.ReadAllLines(progressPath).Length);

            var reloaded = Assert.Single(fixture.Store.LoadAll());
            Assert.Equal(1, reloaded.Status.SkippedFiles);
            Assert.Equal("skipped-fresh", reloaded.Progress[^1].State);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Skipped_fresh_aggregate_is_durable_and_is_not_repeated_after_restart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        var executor = new CapturingExecutorFactory();
        try
        {
            var stored = fixture.CreateRun("aggregate-skipped", "queued", "project");
            fixture.Store.AppendProgress(new ReviewRunFileTransition(
                "Sample.cs", "skipped-fresh", stored.Manifest.CreatedAt, DateTimeOffset.UtcNow,
                stored.Manifest.RunId, null));
            fixture.Store.WriteStatus(stored.Status with
            {
                State = "running",
                CompletedFiles = 1,
                SkippedFiles = 1,
                Cursor = 1,
                StartedAt = stored.Manifest.CreatedAt,
                AggregateState = "skipped-fresh",
            });
            var progressPath = fixture.ProgressPath(stored.Manifest.RunId);
            var transitionsBefore = File.ReadAllLines(progressPath).Length;

            await using var application = fixture.CreateApplication(executor);
            using var client = application.CreateClient();
            var run = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);

            Assert.Equal("skipped-fresh", run.GetProperty("aggregateState").GetString());
            Assert.Empty(executor.Requests);
            Assert.Equal(transitionsBefore, File.ReadAllLines(progressPath).Length);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Recovered_aggregate_run_uses_exclusions_from_the_durable_manifest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        var executor = new CapturingExecutorFactory();
        try
        {
            var exclusion = new ScopeExclusion("Generated.cs", "Generated source");
            var stored = fixture.CreateRun("excluded-resume", "queued", "project", [exclusion]);

            await using var application = fixture.CreateApplication(executor);
            using var client = application.CreateClient();
            var run = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);

            Assert.Equal("done", run.GetProperty("aggregateState").GetString());
            var aggregate = Assert.Single(executor.Requests, request => request.Level == ReviewLevel.Project);
            Assert.Equal(exclusion, Assert.Single(aggregate.AggregateExclusions!));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task File_running_at_crash_is_requeued_and_attempted_again()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("mid-file", "queued");
            fixture.Store.AppendProgress(new ReviewRunFileTransition(
                "Sample.cs", "running", DateTimeOffset.UtcNow, null, stored.Manifest.RunId, null));
            fixture.Store.WriteStatus(stored.Status with
            {
                State = "running",
                StartedAt = DateTimeOffset.UtcNow,
            });
            await File.AppendAllTextAsync(fixture.ProgressPath(stored.Manifest.RunId), "{\"path\":", cancellationToken);

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var run = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);

            Assert.Equal("failed", Assert.Single(run.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
            var transitions = fixture.Store.LoadAll().Single().Progress.Select(progress => progress.State).ToArray();
            Assert.Equal(["queued", "running", "queued", "running", "failed"], transitions);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Terminal_run_is_loaded_but_not_resumed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("terminal", "done");
            var progressPath = fixture.ProgressPath(stored.Manifest.RunId);
            var progressBefore = await File.ReadAllTextAsync(progressPath, cancellationToken);

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/review/runs/{stored.Manifest.RunId}", cancellationToken);
            await Task.Delay(100, cancellationToken);

            Assert.Equal("done", run.GetProperty("state").GetString());
            Assert.Equal(progressBefore, await File.ReadAllTextAsync(progressPath, cancellationToken));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("capped")]
    public async Task Recovered_terminal_states_publish_explicit_partial_canonical_reports(string state)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("terminal-report", state);

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var run = await client.GetFromJsonAsync<JsonElement>(
                $"/api/review/runs/{stored.Manifest.RunId}", cancellationToken);
            var report = new QualityRunReportStore(fixture.RepositoryRoot).Load(stored.Manifest.RunId);

            Assert.Equal(state, run.GetProperty("state").GetString());
            Assert.Equal(state, report.Run.State);
            Assert.Equal("partial", report.Run.Completeness);
            Assert.NotNull(report.Summary.PartialReason);
            Assert.Single(report.Observations);
            Assert.Null(report.Observations[0].SidecarSha256);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Paused_run_waits_for_an_explicit_resume()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("paused", "paused");
            var progressPath = fixture.ProgressPath(stored.Manifest.RunId);
            var progressBefore = await File.ReadAllTextAsync(progressPath, cancellationToken);

            await using var application = fixture.CreateApplication();
            using var client = application.CreateClient();
            var paused = await client.GetFromJsonAsync<JsonElement>(
                $"/api/review/runs/{stored.Manifest.RunId}", cancellationToken);
            await Task.Delay(100, cancellationToken);

            Assert.Equal("paused", paused.GetProperty("state").GetString());
            Assert.Equal(progressBefore, await File.ReadAllTextAsync(progressPath, cancellationToken));

            using var response = await client.PostAsJsonAsync(
                $"/api/review/runs/{stored.Manifest.RunId}/resume", new { }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var resumed = await WaitForStateAsync(client, stored.Manifest.RunId, "done", cancellationToken);
            Assert.Equal("failed", Assert.Single(resumed.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Status_replacement_always_leaves_a_complete_latest_document()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await DurableRunFixture.CreateAsync(cancellationToken);
        try
        {
            var stored = fixture.CreateRun("atomic", "queued");
            var statusPath = Path.Combine(fixture.Store.RunsPath, stored.Manifest.RunId, "status.json");
            for (var version = 1; version <= 50; version++)
            {
                fixture.Store.WriteStatus(stored.Status with { UsageOperations = version });
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath, cancellationToken));
                Assert.Equal(version, document.RootElement.GetProperty("usageOperations").GetInt32());
            }

            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(statusPath)!, "status.*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<JsonElement> WaitForStateAsync(
        HttpClient client,
        string runId,
        string expected,
        CancellationToken cancellationToken)
    {
        JsonElement run = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            if (run.GetProperty("state").GetString() == expected) return run;
            await Task.Delay(20, cancellationToken);
        }
        return run;
    }

    private static ReviewExecutionResult CapturedExecution(
        ReviewRequest request,
        bool skippedFresh,
        int sequence = 1)
    {
        var fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{request.FilePath}\0{request.Level}\0{sequence}")));
        var reviewedHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{request.UnitId}\0{request.FilePath}")));
        var metadata = new JsonObject
        {
            ["$schema"] = ReviewMetaDocument.SchemaId,
            ["schemaVersion"] = ReviewMetaDocument.CurrentSchemaVersion,
            ["unit"] = new JsonObject
            {
                ["id"] = request.UnitId ?? "unit-test",
                ["level"] = request.Level.ToString().ToLowerInvariant(),
                ["path"] = request.FilePath.Replace('\\', '/'),
            },
            ["reviewedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["kind"] = request.Kind,
            ["reviewer"] = new JsonObject { ["runId"] = $"provider-{sequence}" },
            ["reviewedHash"] = new JsonObject { ["value"] = reviewedHash },
            ["grade"] = new JsonObject
            {
                ["score"] = 84,
                ["band"] = "B",
                ["rationale"] = "Captured test observation.",
            },
            ["summary"] = "Captured test observation.",
            ["findings"] = new JsonArray(new JsonObject
            {
                ["id"] = $"finding-{sequence}",
                ["ruleId"] = "quality.test",
                ["aspect"] = "correctness",
                ["severity"] = "medium",
                ["title"] = "Captured finding",
                ["description"] = "Captured before progress completion.",
                ["recommendation"] = "Keep the snapshot.",
                ["fingerprint"] = fingerprint,
                ["locations"] = new JsonArray(new JsonObject { ["path"] = request.FilePath.Replace('\\', '/') }),
            }),
        };
        var json = metadata.ToJsonString() + Environment.NewLine;
        var sidecarHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var sidecarName = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.FilePath)))[..16];
        var snapshot = new ReviewObservationSnapshot(
            $".quality/reviews/captured/{sidecarName}.review-meta.{request.Kind}.json",
            sidecarHash,
            DateTimeOffset.UtcNow,
            json,
            new Dictionary<string, string>(StringComparer.Ordinal) { [fingerprint] = "open" });
        return new ReviewExecutionResult(skippedFresh, null, snapshot);
    }

    private sealed class DurableRunFixture : IDisposable
    {
        private DurableRunFixture(string repositoryRoot, string hostRoot)
        {
            RepositoryRoot = repositoryRoot;
            HostRoot = hostRoot;
            Store = new ReviewRunStore(repositoryRoot);
        }

        public string RepositoryRoot { get; }
        public string HostRoot { get; }
        public ReviewRunStore Store { get; }

        public static async Task<DurableRunFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-run-store-tests", id, "repository");
            var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-run-store-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample; public static class Subject { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Second.cs"),
                "namespace Sample; public static class Second { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new DurableRunFixture(repositoryRoot, hostRoot);
        }

        public StoredReviewRun CreateRun(
            string suffix,
            string state,
            string level = "file",
            IReadOnlyList<ScopeExclusion>? aggregateExclusions = null)
        {
            var runId = $"review-{suffix}-{Guid.NewGuid():N}";
            var createdAt = DateTimeOffset.UtcNow;
            var aggregate = string.Equals(level, "project", StringComparison.Ordinal);
            var manifest = new ReviewRunManifest(
                runId,
                RepositoryRegistry.DefaultRepositoryId,
                new ReviewRunPlanNode(
                    aggregate ? "project-sample" : "file-sample",
                    aggregate ? "Sample" : "Sample.cs",
                    aggregate ? "." : "Sample.cs"),
                level,
                "code",
                null,
                "adapter-that-does-not-exist",
                createdAt,
                [new ReviewRunPlanTarget(
                    "file-sample", "Sample.cs", "Sample.cs",
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
                aggregate ? [] : null,
                aggregateExclusions);
            var status = new ReviewRunStatus(
                runId,
                state,
                1,
                state == "done" ? 1 : 0,
                0,
                state == "done" ? 1 : 0,
                createdAt,
                state == "done" ? createdAt : null,
                state == "done" ? createdAt : null,
                [],
                0,
                new TokenUsage(null, null, null, null, 0));
            Store.Create(manifest, status);
            return new StoredReviewRun(manifest, status, Store.LoadAll().Single().Progress);
        }

        public string ProgressPath(string runId) => Path.Combine(Store.RunsPath, runId, "progress.jsonl");

        public TestApplication CreateApplication(IReviewExecutorFactory? executorFactory = null) =>
            new(RepositoryRoot, HostRoot, executorFactory);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(RepositoryRoot)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TestApplication(
        string repositoryRoot, string contentRoot, IReviewExecutorFactory? executorFactory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:1"] = repositoryRoot,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                if (executorFactory is not null)
                {
                    services.RemoveAll<IReviewExecutorFactory>();
                    services.AddSingleton(executorFactory);
                }
            });
        }
    }

    private sealed class CappedExecutorFactory : IReviewExecutorFactory
    {
        private int operationCount;
        public int OperationCount => operationCount;
        public string? CliType { get; private set; }
        public string? Model { get; private set; }
        public string? ThinkingLevel { get; private set; }

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded)
        {
            CliType = cliType;
            Model = model;
            ThinkingLevel = thinkingLevel;
            return new CappedExecutor(this, cliType, model, usageRecorded);
        }

        private sealed class CappedExecutor(
            CappedExecutorFactory owner, string cliType, string? model, Action<ReviewUsageEntry> usageRecorded) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken)
            {
                var operation = Interlocked.Increment(ref owner.operationCount);
                var entry = new ReviewUsageEntry($"test-{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
                    model ?? "claude-sonnet-5", cliType, new TokenUsage(6, 4, 0, 0, 1),
                    request.Kind, request.Level.ToString().ToLowerInvariant(), request.FilePath);
                await UsageLedger.AppendAsync(request.RepositoryRoot!, entry, cancellationToken);
                usageRecorded(entry);
                return CapturedExecution(request, skippedFresh: false, operation);
            }
        }
    }

    private sealed class FreshnessExecutorFactory : IReviewExecutorFactory
    {
        private int agentCalls;
        public int AgentCalls => agentCalls;

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
            Action<ReviewUsageEntry> usageRecorded) => new FreshnessExecutor(this);

        private sealed class FreshnessExecutor(FreshnessExecutorFactory owner) : IReviewExecutor
        {
            public Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken)
            {
                var operation = force ? Interlocked.Increment(ref owner.agentCalls) : 0;
                return Task.FromResult(CapturedExecution(request, skippedFresh: !force, operation));
            }
        }
    }

    private sealed class CapturingExecutorFactory : IReviewExecutorFactory
    {
        private readonly List<ReviewRequest> requests = [];
        public IReadOnlyList<ReviewRequest> Requests
        {
            get
            {
                lock (requests) return requests.ToArray();
            }
        }

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel, Action<string, CliRunEvent> eventObserver,
            Action<ReviewUsageEntry> usageRecorded) => new CapturingExecutor(requests);

        private sealed class CapturingExecutor(List<ReviewRequest> requests) : IReviewExecutor
        {
            public Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken)
            {
                lock (requests) requests.Add(request);
                return Task.FromResult(CapturedExecution(request, skippedFresh: false));
            }
        }
    }
}
