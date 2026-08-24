using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Availability of the single-reader review queue (concept finding N-01, mitigation M-1). Every
/// fixture here models the same hostile reviewer: one that ignores its cancellation token and
/// never returns. The service must stay usable anyway.
/// </summary>
public sealed class ReviewQueueResilienceTests
{
    [Fact]
    public async Task Cancelling_a_wedged_run_lets_the_next_queued_run_start()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        var wedged = new WedgedExecutorFactory(attaches: true);
        // Long operation budget: the reader must be freed by the cancel itself, not by the
        // per-operation watchdog, or this test would prove the wrong mechanism.
        await using var application = fixture.CreateApplication(wedged, new Dictionary<string, string?>
        {
            ["ReviewJobs:OperationTimeoutSeconds"] = "600",
            ["ReviewJobs:OperationStartupTimeoutSeconds"] = "600",
            ["ReviewJobs:QueueReclaimGraceSeconds"] = "1",
        });
        using var client = application.CreateClient();

        var first = await StartRunAsync(client, "Sample.cs", cancellationToken);
        await wedged.FirstOperation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var second = await StartRunAsync(client, "Second.cs", cancellationToken);
        Assert.Equal("queued", await WaitForStateAsync(client, second, "queued", cancellationToken));

        using var cancel = await client.DeleteAsync($"/api/review/runs/{first}", cancellationToken);
        cancel.EnsureSuccessStatusCode();

        // The wedged reviewer for the first run is still parked; the second run must start anyway.
        Assert.Equal("running", await WaitForStateAsync(client, second, "running", cancellationToken));
        Assert.Equal("cancelled", await WaitForStateAsync(client, first, "cancelled", cancellationToken));
        var cancelled = await GetRunAsync(client, first, cancellationToken);
        Assert.All(cancelled.GetProperty("files").EnumerateArray(),
            file => Assert.Equal("cancelled", file.GetProperty("state").GetString()));
        Assert.Equal(2, wedged.Operations);
    }

    [Fact]
    public async Task A_reviewer_that_stops_making_progress_is_reclaimed_and_the_file_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        var wedged = new WedgedExecutorFactory(attaches: true);
        await using var application = fixture.CreateApplication(wedged, new Dictionary<string, string?>
        {
            ["ReviewJobs:OperationTimeoutSeconds"] = "1",
            ["ReviewJobs:OperationStartupTimeoutSeconds"] = "1",
            ["ReviewJobs:OperationAbandonGraceSeconds"] = "0",
        });
        using var client = application.CreateClient();

        var runId = await StartRunAsync(client, "Sample.cs", cancellationToken);

        // No cancel and no operator: the watchdog alone must drive this to a terminal state.
        Assert.Equal("done", await WaitForStateAsync(client, runId, "done", cancellationToken));
        var run = await GetRunAsync(client, runId, cancellationToken);
        var file = Assert.Single(run.GetProperty("files").EnumerateArray());
        Assert.Equal("failed", file.GetProperty("state").GetString());
        Assert.Contains("wall-clock budget", file.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Equal(1, run.GetProperty("failedFiles").GetInt32());
    }

    [Fact]
    public async Task A_reviewer_that_never_attaches_is_failed_as_never_attached()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        var wedged = new WedgedExecutorFactory(attaches: false);
        await using var application = fixture.CreateApplication(wedged, new Dictionary<string, string?>
        {
            ["ReviewJobs:OperationTimeoutSeconds"] = "600",
            ["ReviewJobs:OperationStartupTimeoutSeconds"] = "1",
            ["ReviewJobs:OperationAbandonGraceSeconds"] = "0",
        });
        using var client = application.CreateClient();

        var runId = await StartRunAsync(client, "Sample.cs", cancellationToken);

        Assert.Equal("done", await WaitForStateAsync(client, runId, "done", cancellationToken));
        var run = await GetRunAsync(client, runId, cancellationToken);
        var file = Assert.Single(run.GetProperty("files").EnumerateArray());
        Assert.Equal("failed", file.GetProperty("state").GetString());
        Assert.Contains("never attached", file.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_health_reports_the_parked_reader_and_the_oldest_running_operation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        var wedged = new WedgedExecutorFactory(attaches: true);
        await using var application = fixture.CreateApplication(wedged, new Dictionary<string, string?>
        {
            ["ReviewJobs:OperationTimeoutSeconds"] = "600",
            ["ReviewJobs:OperationStartupTimeoutSeconds"] = "600",
        });
        using var client = application.CreateClient();

        var idle = await client.GetFromJsonAsync<JsonElement>("/api/review/queue", cancellationToken);
        Assert.False(idle.GetProperty("readerParked").GetBoolean());
        Assert.Equal(600, idle.GetProperty("operationTimeoutSeconds").GetInt32());

        var runId = await StartRunAsync(client, "Sample.cs", cancellationToken);
        await wedged.FirstOperation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var queued = await StartRunAsync(client, "Second.cs", cancellationToken);

        var health = await client.GetFromJsonAsync<JsonElement>("/api/review/queue", cancellationToken);

        Assert.True(health.GetProperty("readerParked").GetBoolean());
        Assert.Equal(runId, health.GetProperty("readerRunId").GetString());
        Assert.Equal(runId, health.GetProperty("oldestRunningRunId").GetString());
        Assert.Equal("Sample.cs", health.GetProperty("oldestRunningFilePath").GetString());
        Assert.True(health.GetProperty("oldestRunningFileAgeSeconds").GetDouble() >= 0);
        Assert.Equal(1, health.GetProperty("queuedRuns").GetInt32());
        Assert.Equal(1, health.GetProperty("runningRuns").GetInt32());
        Assert.Equal(queued, (await GetRunAsync(client, queued, cancellationToken)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Startup_recovery_releases_an_operation_a_terminal_run_left_running()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        // A host that stops between the durable terminal state and the per-file release leaves
        // exactly this shape behind: a cancelled run still claiming a running file.
        var runId = fixture.SeedRun("orphan", runState: "cancelled", fileState: "running");

        await using var application = fixture.CreateApplication();
        using var client = application.CreateClient();
        var run = await GetRunAsync(client, runId, cancellationToken);

        Assert.Equal("cancelled", run.GetProperty("state").GetString());
        var file = Assert.Single(run.GetProperty("files").EnumerateArray());
        Assert.Equal("cancelled", file.GetProperty("state").GetString());
        Assert.NotNull(file.GetProperty("finishedAt").GetString());
        var transitions = fixture.Store.LoadAll().Single().Progress.Select(entry => entry.State).ToArray();
        Assert.Equal("cancelled", transitions[^1]);
    }

    private static async Task<string> StartRunAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path,
            kind = "code",
            cliType = "test-agent",
            model = "claude-sonnet-5",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return accepted.GetProperty("id").GetString()!;
    }

    private static Task<JsonElement> GetRunAsync(HttpClient client, string runId, CancellationToken cancellationToken) =>
        client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);

    private static async Task<string?> WaitForStateAsync(
        HttpClient client, string runId, string expected, CancellationToken cancellationToken)
    {
        string? state = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            state = (await GetRunAsync(client, runId, cancellationToken)).GetProperty("state").GetString();
            if (state == expected) return state;
            await Task.Delay(50, cancellationToken);
        }
        return state;
    }

    private sealed class QueueFixture : IDisposable
    {
        private QueueFixture(string repositoryRoot, string hostRoot)
        {
            RepositoryRoot = repositoryRoot;
            HostRoot = hostRoot;
            Store = new ReviewRunStore(repositoryRoot);
        }

        public string RepositoryRoot { get; }
        public string HostRoot { get; }
        public ReviewRunStore Store { get; }

        public static async Task<QueueFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), "quality-studio-queue-tests", id);
            var repositoryRoot = Path.Combine(root, "repository");
            var hostRoot = Path.Combine(root, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample; public static class Subject { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Second.cs"),
                "namespace Sample; public static class Second { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new QueueFixture(repositoryRoot, hostRoot);
        }

        public string SeedRun(string suffix, string runState, string fileState)
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
                null,
                null);
            Store.Create(manifest, new ReviewRunStatus(
                runId, runState, 1, 0, 0, 0, createdAt, createdAt, createdAt, [], 0,
                new TokenUsage(null, null, null, null, 0)));
            Store.AppendProgress(new ReviewRunFileTransition(
                "Sample.cs", fileState, createdAt, null, runId, null));
            return runId;
        }

        public TestApplication CreateApplication(
            IReviewExecutorFactory? executorFactory = null,
            IReadOnlyDictionary<string, string?>? settings = null) =>
            new(RepositoryRoot, HostRoot, executorFactory, settings);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(RepositoryRoot)!, recursive: true);
            }
            catch (IOException)
            {
                // A wedged fixture operation may still hold a handle; the temp root is disposable.
            }
        }
    }

    private sealed class TestApplication(
        string repositoryRoot,
        string contentRoot,
        IReviewExecutorFactory? executorFactory,
        IReadOnlyDictionary<string, string?>? settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = repositoryRoot,
                };
                foreach (var setting in settings ?? new Dictionary<string, string?>()) values[setting.Key] = setting.Value;
                configuration.AddInMemoryCollection(values);
            });
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

    /// <summary>
    /// The N-01 reviewer: it starts, optionally reports that it attached, and then never returns
    /// and never honours its cancellation token. Nothing but an external deadline can end it.
    /// </summary>
    private sealed class WedgedExecutorFactory(bool attaches) : IReviewExecutorFactory
    {
        private readonly TaskCompletionSource firstOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool attaches = attaches;
        private int operations;

        public int Operations => Volatile.Read(ref operations);
        public Task FirstOperation => firstOperation.Task;

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new WedgedExecutor(this, cliType, model, eventObserver);

        private sealed class WedgedExecutor(
            WedgedExecutorFactory owner,
            string cliType,
            string? model,
            Action<string, CliRunEvent> eventObserver) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.operations);
                if (owner.attaches) eventObserver(cliType, new CliRunEvent.RunStarted(4242, cliType, model));
                owner.firstOperation.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
                return null!;
            }
        }
    }
}
