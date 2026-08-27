using System.Net;
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
/// Regression coverage for the 2026-08-18 hardening finding (QS-93, dossier M-1): a
/// cancelled review must not wedge <see cref="ReviewJobService"/>'s single-reader queue
/// behind it — a later-enqueued run must still leave "queued".
/// </summary>
public sealed class ReviewQueueReclaimTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-reclaim-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-reclaim-hosts", Guid.NewGuid().ToString("N"));

    // Both scenarios run sequentially in one test — deliberately, rather than as two
    // [Fact]s — so this never depends on the test runner's parallelization settings.
    // Two WebApplicationFactory<Program> instances (each with a live BackgroundService)
    // racing each other under xunit's default collection parallelism was observed to
    // cause spurious 500s unrelated to the behaviour under test.
    [Fact]
    public async Task Cancelling_a_run_lets_the_next_queued_run_advance()
    {
        await CooperativeCancellationAdvancesQuicklyAsync();
        await ReclaimBackstopAdvancesWhenTheReviewerIgnoresCancellationAsync();
    }

    private async Task CooperativeCancellationAdvancesQuicklyAsync()
    {
        Directory.CreateDirectory(hostRoot + "-cooperative");
        await using var application = new ReclaimTestApplication(repositoryRoot + "-cooperative", hostRoot + "-cooperative",
            respectsCancellation: true, cancelReclaimGraceSeconds: 45);
        await SeedRepositoryAsync(repositoryRoot + "-cooperative");
        using var client = application.CreateClient();

        var first = await StartReviewAsync(client, "code");
        await WaitForStateAsync(client, first, "running", TimeSpan.FromSeconds(10));
        var second = await StartReviewAsync(client, "security");
        Assert.Equal("queued", await GetStateAsync(client, second));

        using var cancel = await client.DeleteAsync($"/api/review/runs/{first}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        // A cooperative reviewer honours cancellation almost immediately, well inside the
        // 45s reclaim grace period configured above — this proves the fast path, not the backstop.
        await WaitForStateNotAsync(client, second, "queued", TimeSpan.FromSeconds(10));
    }

    private async Task ReclaimBackstopAdvancesWhenTheReviewerIgnoresCancellationAsync()
    {
        Directory.CreateDirectory(hostRoot + "-backstop");
        await using var application = new ReclaimTestApplication(repositoryRoot + "-backstop", hostRoot + "-backstop",
            respectsCancellation: false, cancelReclaimGraceSeconds: 2);
        await SeedRepositoryAsync(repositoryRoot + "-backstop");
        using var client = application.CreateClient();

        var first = await StartReviewAsync(client, "code");
        await WaitForStateAsync(client, first, "running", TimeSpan.FromSeconds(10));
        var second = await StartReviewAsync(client, "security");
        Assert.Equal("queued", await GetStateAsync(client, second));

        using var cancel = await client.DeleteAsync($"/api/review/runs/{first}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        // The fake reviewer never observes cancellation; only the queue's reclaim backstop
        // (armed ~2s after Cancel()) can free the reader loop to pick up the next run.
        await WaitForStateNotAsync(client, second, "queued", TimeSpan.FromSeconds(15));
    }

    private static async Task<string> StartReviewAsync(HttpClient client, string kind)
    {
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind,
            cliType = "codex",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return accepted.GetProperty("id").GetString()!;
    }

    private static async Task<string> GetStateAsync(HttpClient client, string id)
    {
        using var response = await client.GetAsync($"/api/review/runs/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return run.GetProperty("state").GetString()!;
    }

    private static async Task WaitForStateAsync(HttpClient client, string id, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await GetStateAsync(client, id) == state) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
        Assert.Fail($"Review {id} did not reach state '{state}' within {timeout}.");
    }

    private static async Task WaitForStateNotAsync(HttpClient client, string id, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = await GetStateAsync(client, id);
            if (current != state) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
        Assert.Fail($"Review {id} was still in state '{state}' after {timeout}.");
    }

    private static async Task SeedRepositoryAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"),
            "namespace Sample; public static class Greeter { public static string Hello() => \"hello\"; }");
        using var init = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            },
        };
        init.Start();
        await init.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { "-cooperative", "-backstop" })
        {
            try
            {
                Directory.Delete(repositoryRoot + suffix, true);
                Directory.Delete(hostRoot + suffix, true);
            }
            catch (IOException)
            {
            }
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ReclaimTestApplication(
        string root, string contentRoot, bool respectsCancellation, double cancelReclaimGraceSeconds)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = Path.GetDirectoryName(root),
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                    ["ReviewJobs:CancelReclaimGraceSeconds"] = cancelReclaimGraceSeconds.ToString(),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton<IReviewExecutorFactory>(new HangingReviewExecutorFactory(respectsCancellation));
            });
        }
    }

    // A reviewer stand-in that never finishes: the current attempt hangs forever, either
    // honouring cancellation (the common, cooperative case) or ignoring it entirely (the
    // pathological case the queue's own reclaim backstop has to cover on its own).
    private sealed class HangingReviewExecutorFactory(bool respectsCancellation) : IReviewExecutorFactory
    {
        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new HangingExecutor(respectsCancellation);

        private sealed class HangingExecutor(bool respectsCancellation) : IReviewExecutor
        {
            public async Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request, bool force, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, respectsCancellation ? cancellationToken : CancellationToken.None)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            }
        }
    }
}
