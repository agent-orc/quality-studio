using System.Diagnostics;
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
/// Sweep-level cover for the security preflight: one <c>kind=security</c> run over several files
/// collects sensor evidence once for the whole sweep instead of rescanning the repository per file.
/// </summary>
public sealed class SecurityPreflightSweepTests
{
    [Fact]
    public async Task Security_sweep_runs_each_sensor_once_for_the_whole_run()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = await SweepFixture.CreateAsync(cancellationToken);
        var sensor = new CountingSecuritySensor();
        try
        {
            await using var application = fixture.CreateApplication(sensor);
            using var client = application.CreateClient();

            using var response = await client.PostAsJsonAsync("/api/review", new
            {
                path = ".",
                kind = "security",
                cliType = "test-agent",
                model = "claude-sonnet-5",
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal(2, accepted.GetProperty("totalFiles").GetInt32());

            var run = await WaitForTerminalStateAsync(
                client, accepted.GetProperty("id").GetString()!, cancellationToken);

            Assert.Equal("done", run.GetProperty("state").GetString());
            // The sweep reviewed both files plus the aggregate, so the single sensor run below
            // cannot be explained by the sweep having reviewed nothing.
            Assert.Equal(2, run.GetProperty("completedFiles").GetInt32());
            Assert.Equal("done", run.GetProperty("aggregateState").GetString());
            Assert.Equal(3, run.GetProperty("usageOperations").GetInt32());
            Assert.Equal(1, sensor.Runs);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<JsonElement> WaitForTerminalStateAsync(
        HttpClient client,
        string runId,
        CancellationToken cancellationToken)
    {
        JsonElement run = default;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{runId}", cancellationToken);
            var state = run.GetProperty("state").GetString();
            if (state is "done" or "failed" or "cancelled" or "capped") return run;
            await Task.Delay(20, cancellationToken);
        }

        return run;
    }

    private sealed class SweepFixture : IDisposable
    {
        private SweepFixture(string repositoryRoot, string hostRoot)
        {
            RepositoryRoot = repositoryRoot;
            HostRoot = hostRoot;
        }

        public string RepositoryRoot { get; }

        public string HostRoot { get; }

        public static async Task<SweepFixture> CreateAsync(CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid().ToString("N");
            var repositoryRoot = Path.Combine(
                Path.GetTempPath(), "quality-studio-preflight-sweep-tests", id, "repository");
            var hostRoot = Path.Combine(
                Path.GetTempPath(), "quality-studio-preflight-sweep-tests", id, "host");
            Directory.CreateDirectory(repositoryRoot);
            Directory.CreateDirectory(hostRoot);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"),
                "namespace Sample; public static class Subject { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Second.cs"),
                "namespace Sample; public static class Second { }", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", cancellationToken);
            // The preflight snapshot is only reusable inside a git working tree: without a
            // verifiable source state the fingerprint is deliberately unrepeatable.
            Git(repositoryRoot, "init", "--quiet");
            Git(repositoryRoot, "config", "user.email", "tests@quality-studio.invalid");
            Git(repositoryRoot, "config", "user.name", "Quality Studio Tests");
            Git(repositoryRoot, "add", ".");
            Git(repositoryRoot, "commit", "--quiet", "-m", "sweep fixture");
            return new SweepFixture(repositoryRoot, hostRoot);
        }

        public TestApplication CreateApplication(IReviewSensor sensor) =>
            new(RepositoryRoot, HostRoot, sensor);

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

        private static void Git(string root, params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    private sealed class TestApplication(
        string repositoryRoot, string contentRoot, IReviewSensor sensor) : WebApplicationFactory<Program>
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
                // The repository registry seeds its sensor selection from the registered sensors, so
                // the counting sensor is the one enabled security sensor for this repository.
                services.RemoveAll<IReviewSensor>();
                services.AddSingleton(sensor);
                // Keep the real review runner (it owns the snapshot reuse) and fake only the agent.
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton<IReviewExecutorFactory>(provider => new RunnerExecutorFactory(
                    provider.GetRequiredService<SensorRegistry>(),
                    provider.GetRequiredService<StalenessEvaluator>()));
            });
        }
    }

    private sealed class RunnerExecutorFactory(SensorRegistry sensors, StalenessEvaluator staleness)
        : IReviewExecutorFactory
    {
        public IReviewExecutor Create(string cliType, string? model, string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver, Action<ReviewUsageEntry> usageRecorded) =>
            new RunnerExecutor(new ReviewRunner(
                new FakeReviewAgent(),
                usageRecorded: usageRecorded,
                sensorRegistry: sensors,
                stalenessEvaluator: staleness));

        private sealed class RunnerExecutor(ReviewRunner runner) : IReviewExecutor
        {
            public Task<ReviewExecutionResult> ReviewIfNeededAsync(
                ReviewRequest request,
                bool force,
                CancellationToken cancellationToken) =>
                runner.ReviewIfNeededAsync(request, force, cancellationToken);
        }
    }

    /// <summary>Counts how often the sweep actually invokes the security sensor.</summary>
    private sealed class CountingSecuritySensor : IReviewSensor
    {
        private int runs;

        public int Runs => Volatile.Read(ref runs);

        public string Id => "gitleaks";

        public string Version => "8.24.2";

        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true,
                ToolVersions: new Dictionary<string, string> { ["gitleaks"] = Version }));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref runs);
            var finding = new ReviewFinding(
                "gitleaks-sample",
                "secrets",
                FindingSeverity.High,
                "Planted test secret",
                "Gitleaks detected a high-confidence test secret.",
                "Remove and rotate the credential.",
                [new FindingLocation("Sample.cs",
                    new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 8)))],
                "sha256:" + new string('a', 64),
                "generic-api-key",
                Source: new FindingSource(FindingSourceKind.Deterministic, "gitleaks", "gitleaks", Version));
            return Task.FromResult(new SensorScanResult(
                true,
                null,
                [finding],
                new SensorProvenance(Id, Version, "repository", ".", "2026-08-27T10:00:00.000Z",
                    new Dictionary<string, string> { ["gitleaks"] = Version })));
        }
    }

    private sealed class FakeReviewAgent : IReviewAgent
    {
        private const string Response = """
            {
              "grade": { "score": 90, "band": "A", "rationale": "No agent-authored defect." },
              "summary": "No agent-authored defect.",
              "aspects": [
                { "id": "secrets", "title": "Secrets", "grade": { "score": 90, "band": "A", "rationale": "Clean." } }
              ],
              "findings": []
            }
            """;

        public string AgentName => "test-agent";

        public string? Model => "claude-sonnet-5";

        public Task<ReviewAgentResult> RunAsync(
            string prompt,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewAgentResult($"run-{Guid.NewGuid():N}",
                $"```json\n{Response}\n```",
                new TokenUsage(120, 34, 56, 7, 890), "claude-sonnet-5"));
    }
}
