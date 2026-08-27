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
/// Regression cover for the queue half of M-1. The 2026-08-18 review session found a cancelled
/// run that left the review queue permanently wedged: cancelling set the run's terminal state but
/// the single-reader loop stayed blocked on the abandoned attempt, so a later-enqueued run never
/// left "queued". See docs/operations/security-concept/ finding N-01.
/// </summary>
public sealed class ReviewQueueReclaimTests
{
    [Fact]
    public async Task Queue_advances_to_the_next_run_after_a_cancelled_run_refuses_to_unwind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await QueueFixture.CreateAsync(cancellationToken);
        using var stuck = new StuckExecutorFactory();
        await using var application = fixture.CreateApplication(stuck);
        using var client = application.CreateClient();

        var first = await StartReviewAsync(client, cancellationToken);
        await WaitForStateAsync(client, first, "running", cancellationToken);

        // The reviewer for the first run never returns and never observes cancellation, so
        // without the reclaim path the queue reader stays blocked on it forever.
        using var cancel = await client.DeleteAsync($"/api/review/runs/{first}", cancellationToken);
        cancel.EnsureSuccessStatusCode();

        var second = await StartReviewAsync(client, cancellationToken);
        var run = await WaitForStateAsync(client, second, "running", cancellationToken);

        Assert.Equal("running", run.GetProperty("state").GetString());

        // A run flips to "running" a moment before the executor is actually invoked, so this has
        // to be waited for rather than sampled.
        await WaitForAsync(() => stuck.Started == 2,
            "the second run's reviewer was never invoked", cancellationToken);

        // Let the abandoned attempts unwind before the host shuts down; otherwise disposal blocks
        // on them until the shutdown timeout, which slows the suite and starves parallel tests.
        stuck.Release();
    }

    private static async Task WaitForAsync(Func<bool> condition, string failure, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 1500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail($"Timed out waiting: {failure}.");
    }

    private static async Task<string> StartReviewAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "test-agent",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return accepted.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> WaitForStateAsync(
        HttpClient client, string runId, string expected, CancellationToken cancellationToken)
    {
        JsonElement run = default;
        // Deliberately generous. This is a wedge detector, not a latency budget: a wedged queue
        // never advances at all, so a long bound costs nothing on the passing path and keeps the
        // test from flaking when the suite runs under parallel load.
        for (var attempt = 0; attempt < 1500; attempt++)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            if (run.GetProperty("state").GetString() == expected) return run;
            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail($"Review {runId} never reached '{expected}'; last state was " +
            $"'{run.GetProperty("state").GetString()}'. The queue did not advance.");
        return run;
    }

    /// <summary>
    /// A reviewer that hangs forever and ignores cancellation — the observed failure mode, where
    /// no reviewer CLI process was ever attached and nothing ever completed the operation.
    /// </summary>
    private sealed class StuckExecutorFactory : IReviewExecutorFactory, IDisposable
    {
        private readonly CancellationTokenSource release = new();
        private int started;

        public int Started => Volatile.Read(ref started);

        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new StuckExecutor(this);

        /// <summary>Unblocks the abandoned attempts so the host can shut down promptly.</summary>
        public void Release()
        {
            if (!release.IsCancellationRequested) release.Cancel();
        }

        public void Dispose()
        {
            Release();
            release.Dispose();
        }

        private sealed class StuckExecutor(StuckExecutorFactory owner) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.started);
                // Deliberately waits on the fixture's token, not the caller's: cancelling the run
                // must not be what unblocks the queue, otherwise the test proves nothing.
                await Task.Delay(Timeout.InfiniteTimeSpan, owner.release.Token).ConfigureAwait(false);
                throw new UnreachableException();
            }
        }

        private sealed class UnreachableException() : Exception("The stuck reviewer must never complete.");
    }

    private sealed class QueueFixture(string repositoryRoot, string hostRoot) : IDisposable
    {
        public static async Task<QueueFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-queue-reclaim-tests", id, "repository");
            var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-queue-reclaim-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample; public static class Subject { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new QueueFixture(repositoryRoot, hostRoot);
        }

        public TestApplication CreateApplication(IReviewExecutorFactory executorFactory) =>
            new(repositoryRoot, hostRoot, executorFactory);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(repositoryRoot)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class TestApplication(
        string repositoryRoot, string contentRoot, IReviewExecutorFactory executorFactory)
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
                    // Keep the test fast; the production default is 45s.
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
}
