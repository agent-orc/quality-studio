using System.Net.Http.Json;
using System.Text.Json;
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
                model = "claude-sonnet-4-5",
                tokenCap = 5,
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("test-agent", accepted.GetProperty("cliType").GetString());
            Assert.Equal("claude-sonnet-4-5", accepted.GetProperty("model").GetString());
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

        public StoredReviewRun CreateRun(string suffix, string state)
        {
            var runId = $"review-{suffix}-{Guid.NewGuid():N}";
            var createdAt = DateTimeOffset.UtcNow;
            var manifest = new ReviewRunManifest(
                runId,
                RepositoryRegistry.DefaultRepositoryId,
                new ReviewRunPlanNode("file-sample", "Sample.cs", "Sample.cs"),
                "file",
                "code",
                null,
                "adapter-that-does-not-exist",
                createdAt,
                [new ReviewRunPlanTarget(
                    "file-sample", "Sample.cs", "Sample.cs",
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
                null);
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

        public IReviewExecutor Create(string cliType, string? model, Action<string, CliRunEvent> eventObserver,
            Action<ReviewUsageEntry> usageRecorded) => new CappedExecutor(this, cliType, model, usageRecorded);

        private sealed class CappedExecutor(
            CappedExecutorFactory owner, string cliType, string? model, Action<ReviewUsageEntry> usageRecorded) : IReviewExecutor
        {
            public async Task ReviewAsync(ReviewRequest request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.operationCount);
                var entry = new ReviewUsageEntry($"test-{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
                    model ?? "claude-sonnet-4-5", cliType, new TokenUsage(6, 4, 0, 0, 1),
                    request.Kind, request.Level.ToString().ToLowerInvariant(), request.FilePath);
                await UsageLedger.AppendAsync(request.RepositoryRoot!, entry, cancellationToken);
                usageRecorded(entry);
            }
        }
    }
}
