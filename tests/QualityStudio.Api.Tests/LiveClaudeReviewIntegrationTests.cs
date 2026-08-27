using System.Net.Http.Json;
using System.Text.Json;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// QS-93 M-1 regression, exercised through the full API/queue pipeline rather than the
/// reviewer in isolation (see <c>LiveReviewIntegrationTests.ClaudeReviewerAttaches_WhenExplicitlyEnabled</c>
/// in AgentOrchestrator.CodeQuality.Tests for that). The 2026-08-18 dossier's exact
/// measurement was <c>usageOperations</c> staying at 0 across 7 <c>cliType=claude</c> runs
/// with one held "running" for 16 minutes. This asserts a real run reaches a terminal
/// state within the watchdog's bound and has recorded at least one operation — i.e. the
/// reviewer attached and did something, even if the CLI's reply itself fails a later,
/// unrelated pipeline step (response-contract parsing) in this sandbox.
/// </summary>
public sealed class LiveClaudeReviewIntegrationTests
{
    [Fact]
    public async Task ClaudeReview_ThroughTheApi_RecordsNonZeroUsageOperationsWithinBound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!string.Equals(Environment.GetEnvironmentVariable("QUALITY_RUN_LIVE_REVIEW"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set QUALITY_RUN_LIVE_REVIEW=1 to run the installed Claude CLI integration.");
        }

        var id = Guid.NewGuid().ToString("N");
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-live-claude-tests", id, "repository");
        var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-live-claude-tests", id, "host");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
            "namespace Sample; public static class Subject { public static int Add(int a, int b) => a + b; }",
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);

        try
        {
            await using var application = new TestApplication(repositoryRoot, hostRoot);
            using var client = application.CreateClient();

            using var response = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "code",
                cliType = "claude",
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var runId = accepted.GetProperty("id").GetString()!;

            var run = await WaitForTerminalStateAsync(client, runId, cancellationToken);

            Assert.Contains(run.GetProperty("state").GetString(), new[] { "done", "failed", "cancelled" });
            Assert.True(run.GetProperty("usageOperations").GetInt32() > 0,
                $"expected at least one recorded usage operation; run: {run}");
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(repositoryRoot)!, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task<JsonElement> WaitForTerminalStateAsync(
        HttpClient client, string runId, CancellationToken cancellationToken)
    {
        JsonElement run = default;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            var state = run.GetProperty("state").GetString();
            if (state is "done" or "failed" or "cancelled") return run;
            await Task.Delay(100, cancellationToken);
        }
        return run;
    }

    private sealed class TestApplication(string repositoryRoot, string contentRoot) : WebApplicationFactory<Program>
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
                // Avoid real quota-probe network calls; this test only cares about the
                // reviewer CLI's own attach/usage behavior, not quota gating.
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
            });
        }
    }
}
