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

public sealed class ReviewQueueReclaimTests
{
    [Fact]
    public async Task Queue_advances_past_a_cancelled_run_whose_attempt_never_unwinds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await ReclaimFixture.CreateAsync(cancellationToken);
        var executor = new StallingExecutorFactory();
        try
        {
            await using var application = fixture.CreateApplication(executor, cancelReclaimGraceSeconds: 1);
            using var client = application.CreateClient();

            using var firstResponse = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
            }, cancellationToken);
            firstResponse.EnsureSuccessStatusCode();
            var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var firstId = first.GetProperty("id").GetString()!;

            await WaitForStateAsync(client, firstId, "running", cancellationToken);
            await WaitUntilAsync(() => executor.StartedOperations > 0, cancellationToken);

            using var cancelResponse = await client.DeleteAsync($"/api/review/runs/{firstId}", cancellationToken);
            cancelResponse.EnsureSuccessStatusCode();
            var cancelled = await cancelResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("cancelled", cancelled.GetProperty("state").GetString());

            using var secondResponse = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
            }, cancellationToken);
            secondResponse.EnsureSuccessStatusCode();
            var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var secondId = second.GetProperty("id").GetString()!;

            // The first run's stalled attempt never observes cancellation and never
            // completes, and this fake reviewer never completes any run either — so the
            // only way the second run can leave "queued" at all is if the single-reader
            // queue abandoned the stalled first attempt via the cancel-reclaim backstop.
            var started = await WaitForStateAsync(client, secondId, "running", cancellationToken, attempts: 300);
            Assert.Equal("running", started.GetProperty("state").GetString());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<JsonElement> WaitForStateAsync(
        HttpClient client, string runId, string expected, CancellationToken cancellationToken, int attempts = 100)
    {
        JsonElement run = default;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            if (run.GetProperty("state").GetString() == expected) return run;
            await Task.Delay(20, cancellationToken);
        }
        return run;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken, int attempts = 200)
    {
        for (var attempt = 0; attempt < attempts && !condition(); attempt++)
            await Task.Delay(20, cancellationToken);
    }

    private sealed class ReclaimFixture : IDisposable
    {
        private ReclaimFixture(string repositoryRoot, string hostRoot)
        {
            RepositoryRoot = repositoryRoot;
            HostRoot = hostRoot;
        }

        public string RepositoryRoot { get; }
        public string HostRoot { get; }

        public static async Task<ReclaimFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-reclaim-tests", id, "repository");
            var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-reclaim-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample; public static class Subject { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new ReclaimFixture(repositoryRoot, hostRoot);
        }

        public TestApplication CreateApplication(IReviewExecutorFactory executorFactory, int cancelReclaimGraceSeconds) =>
            new(RepositoryRoot, HostRoot, executorFactory, cancelReclaimGraceSeconds);

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
        string repositoryRoot, string contentRoot, IReviewExecutorFactory executorFactory, int cancelReclaimGraceSeconds)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = repositoryRoot,
                    ["ReviewJobs:CancelReclaimGraceSeconds"] = cancelReclaimGraceSeconds.ToString(),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton(executorFactory);
            });
        }
    }

    /// <summary>An executor whose review call never completes and never observes cancellation — simulates a reviewer attempt that cannot unwind cooperatively.</summary>
    private sealed class StallingExecutorFactory : IReviewExecutorFactory
    {
        private int startedOperations;
        public int StartedOperations => Volatile.Read(ref startedOperations);

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new StallingExecutor(this);

        private sealed class StallingExecutor(StallingExecutorFactory owner) : IReviewExecutor
        {
            public Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.startedOperations);
                return new TaskCompletionSource<ReviewExecutionResult>().Task;
            }
        }
    }
}
