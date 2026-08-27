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
/// QS-93 M-1: the single-reader <c>ReviewJobService</c> queue must advance past a
/// cancelled run even when that run's in-flight attempt never unwinds on its own (the
/// 2026-08-18 dossier finding: a cancelled run set its own terminal state but a
/// later-enqueued run never left "queued", because the queue's reader was still awaiting
/// the stuck attempt). <see cref="HangingExecutorFactory"/> deliberately ignores the
/// cancellation token passed to it, exercising the grace-period reclaim rather than the
/// normal (well-behaved) cancel path.
/// </summary>
public sealed class ReviewQueueReclaimTests
{
    [Fact]
    public async Task Cancel_ThenNewRun_AdvancesPastAStuckAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await ReclaimFixture.CreateAsync(cancellationToken);
        var fake = new HangingExecutorFactory();
        try
        {
            await using var application = fixture.CreateApplication(fake);
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
            // Make sure the fake executor is actually inside (and stuck in) its review
            // call before we cancel — otherwise cancellation could race ahead of it.
            await fake.EnteredReview.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            using var cancelResponse = await client.DeleteAsync($"/api/review/runs/{firstId}", cancellationToken);
            cancelResponse.EnsureSuccessStatusCode();
            await WaitForStateAsync(client, firstId, "cancelled", cancellationToken);

            using var secondResponse = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "test-agent",
            }, cancellationToken);
            secondResponse.EnsureSuccessStatusCode();
            var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var secondId = second.GetProperty("id").GetString()!;

            // Without the reclaim fix, the first (cancelled-but-stuck) attempt wedges the
            // single-reader queue forever and this never leaves "queued".
            var advanced = await WaitForStateAsync(client, secondId, "running", cancellationToken, attempts: 300);
            Assert.Equal("running", advanced.GetProperty("state").GetString());
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

        public TestApplication CreateApplication(IReviewExecutorFactory executorFactory) =>
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
        string repositoryRoot, string contentRoot, IReviewExecutorFactory executorFactory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = repositoryRoot,
                    // Floors to 1s in ReviewJobsOptions regardless — set explicitly so the
                    // test's intent doesn't depend on that implementation detail.
                    ["ReviewJobs:CancelReclaimGraceSeconds"] = "1",
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

    private sealed class HangingExecutorFactory : IReviewExecutorFactory
    {
        public TaskCompletionSource EnteredReview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) => new HangingExecutor(this);

        private sealed class HangingExecutor(HangingExecutorFactory owner) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken)
            {
                owner.EnteredReview.TrySetResult();
                // Deliberately never observes `cancellationToken` — simulates a reviewer
                // operation whose own bounding (see CodingAgentReviewAgent's watchdog)
                // somehow failed, so the queue's grace-period reclaim is what has to save it.
                await new TaskCompletionSource().Task;
                throw new InvalidOperationException("unreachable");
            }
        }
    }
}
