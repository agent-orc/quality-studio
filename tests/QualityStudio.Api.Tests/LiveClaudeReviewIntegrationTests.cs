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
/// Live cover for the exact scenario in docs/operations/security-concept/ finding N-01: seven
/// cliType=claude review runs that produced 0 operations, 0 findings and 0 tokens, one of which
/// held its files "running" for 16 minutes with no reviewer process attached. This drives the real
/// review pipeline end to end — no fake executor — and asserts the two properties that failed
/// then: the run reaches a terminal state within a bound, and it records real operations.
/// Env-gated because it spawns the installed claude CLI.
/// </summary>
public sealed class LiveClaudeReviewIntegrationTests
{
    private static readonly TimeSpan TerminalBound = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task ClaudeReview_ThroughTheApi_RecordsNonZeroUsageOperationsWithinBound()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QUALITY_RUN_LIVE_REVIEW"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set QUALITY_RUN_LIVE_REVIEW=1 to run the installed claude CLI against the review API.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = await LiveFixture.CreateAsync(cancellationToken);
        await using var application = fixture.CreateApplication();
        using var client = application.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "claude",
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var runId = accepted.GetProperty("id").GetString()!;

        var (run, elapsed) = await WaitForTerminalAsync(client, runId, cancellationToken);
        var state = run.GetProperty("state").GetString();
        await WriteEvidenceAsync(run, elapsed, cancellationToken);

        Assert.True(elapsed < TerminalBound,
            $"Review {runId} was still '{state}' after {elapsed.TotalSeconds:0}s. Before M-1 a wedged " +
            "run held its files 'running' for 16 minutes; every operation must now be bounded.");

        // The dossier symptom was a run that looked clean while doing nothing at all. A nonzero
        // operation count is the direct disproof: the reviewer really attached and ran.
        var operations = run.GetProperty("usageOperations").GetInt32();
        Assert.True(operations > 0,
            $"Review {runId} reached '{state}' in {elapsed.TotalSeconds:0.#}s but recorded " +
            $"{operations} operations — the 0-operation symptom from finding N-01.");
    }

    /// <summary>
    /// Dumps the terminal run snapshot when QUALITY_LIVE_EVIDENCE_DIR points somewhere, so a
    /// re-verification of finding N-01 leaves collectable evidence rather than only a green test.
    /// </summary>
    private static async Task WriteEvidenceAsync(JsonElement run, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        var directory = Environment.GetEnvironmentVariable("QUALITY_LIVE_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        var snapshot = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            elapsedSeconds = Math.Round(elapsed.TotalSeconds, 2),
            cliType = "claude",
            run = JsonSerializer.Deserialize<JsonElement>(run.GetRawText()),
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "qs-93-live-claude-run.json"),
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    private static async Task<(JsonElement Run, TimeSpan Elapsed)> WaitForTerminalAsync(
        HttpClient client, string runId, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        JsonElement run = default;
        while (DateTimeOffset.UtcNow - started < TerminalBound + TimeSpan.FromSeconds(30))
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            var state = run.GetProperty("state").GetString();
            if (state is "done" or "failed" or "cancelled" or "capped")
            {
                return (run, DateTimeOffset.UtcNow - started);
            }

            await Task.Delay(250, cancellationToken);
        }

        return (run, DateTimeOffset.UtcNow - started);
    }

    private sealed class LiveFixture(string repositoryRoot, string hostRoot) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;

        public static async Task<LiveFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-live-claude-tests", id, "repository");
            var hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-live-claude-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample;\n\npublic static class Subject\n{\n    public static int Add(int a, int b) => a - b;\n}\n",
                cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            return new LiveFixture(repositoryRoot, hostRoot);
        }

        public TestApplication CreateApplication() => new(RepositoryRoot, hostRoot);

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
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
            });
        }
    }
}
