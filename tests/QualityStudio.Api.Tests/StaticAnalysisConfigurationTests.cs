using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Events;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class StaticAnalysisConfigurationTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-static-analysis", Guid.NewGuid().ToString("N"), "repository");
    private readonly string hostRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-static-analysis", Guid.NewGuid().ToString("N"), "host");
    private StaticAnalysisApplication? application;

    [Fact]
    public async Task Ranked_compiler_checks_are_independently_configured_and_run_before_review()
    {
        using var client = application!.CreateClient();
        using var sensorsResponse = await client.GetAsync("/api/sensors", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, sensorsResponse.StatusCode);
        var payload = await sensorsResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sensors = payload.GetProperty("sensors").EnumerateArray().ToArray();
        var roslyn = Assert.Single(sensors, sensor => sensor.GetProperty("id").GetString() == "roslyn");
        Assert.True(roslyn.GetProperty("enabled").GetBoolean());
        Assert.Equal("dotnet-build", roslyn.GetProperty("configuration").GetProperty("format").GetString());
        var typescript = Assert.Single(sensors, sensor => sensor.GetProperty("id").GetString() == "tsc");
        Assert.True(typescript.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("npx", typescript.GetProperty("configuration").GetProperty("command").GetString(),
            StringComparison.Ordinal);
        Assert.False(Assert.Single(sensors,
            sensor => sensor.GetProperty("id").GetString() == "eslint").GetProperty("enabled").GetBoolean());
        Assert.False(Assert.Single(sensors,
            sensor => sensor.GetProperty("id").GetString() == "sarif").GetProperty("enabled").GetBoolean());

        using var disabledScan = await client.PostAsync(
            "/api/sensors/eslint/scan", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, disabledScan.StatusCode);

        using var accepted = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        var executor = application.Services.GetRequiredService<CapturingReviewExecutorFactory>();
        var request = await executor.Request.Task.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var evidence = Assert.Single(request.DeterministicEvidence!,
            result => result.Provenance.SensorId == "roslyn");
        var finding = Assert.Single(evidence.Findings);
        Assert.Equal("CS1002", finding.RuleId);
        Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
        Assert.Equal("roslyn", finding.Source.SensorId);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryRoot, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryRoot, "Sample.cs"),
            "namespace Sample; public static class Greeter { public static string Hello() => \"hello\"; }",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "frontend"));
        await File.WriteAllTextAsync(
            Path.Combine(repositoryRoot, "frontend", "tsconfig.app.json"),
            "{}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryRoot, "frontend", "package-lock.json"),
            "{}",
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        application = new StaticAnalysisApplication(repositoryRoot, hostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try
        {
            Directory.Delete(Path.GetDirectoryName(repositoryRoot)!, true);
            Directory.Delete(Path.GetDirectoryName(hostRoot)!, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StaticAnalysisApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = Path.GetDirectoryName(root),
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IReviewSensor>();
                services.AddSingleton<IReviewSensor>(new FakeSensor("gitleaks"));
                services.AddSingleton<IReviewSensor>(new FakeSensor("dependencies"));
                services.AddSingleton<IReviewSensor>(new FakeSensor("boundaries"));
                services.AddSingleton<IReviewSensor>(new FakeSensor("coverage"));
                services.AddSingleton<IReviewSensor>(new FakeDeterministicSensor("sarif"));
                services.AddSingleton<IReviewSensor>(new FakeDeterministicSensor("roslyn", WithCompilerFinding: true));
                services.AddSingleton<IReviewSensor>(new FakeDeterministicSensor("eslint"));
                services.AddSingleton<IReviewSensor>(new FakeDeterministicSensor("tsc"));
                services.RemoveAll<IReviewExecutorFactory>();
                services.AddSingleton<CapturingReviewExecutorFactory>();
                services.AddSingleton<IReviewExecutorFactory>(serviceProvider =>
                    serviceProvider.GetRequiredService<CapturingReviewExecutorFactory>());
            });
        }
    }

    private class FakeSensor(string id) : IReviewSensor
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public virtual Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result(request, []));

        protected SensorScanResult Result(
            SensorScanRequest request,
            IReadOnlyList<ReviewFinding> findings) =>
            new(true, null, findings, new SensorProvenance(
                Id,
                Version,
                "repository",
                ".",
                DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string, string>()));
    }

    private sealed class FakeDeterministicSensor(string id, bool WithCompilerFinding = false) :
        FakeSensor(id), IDeterministicEvidenceSensor
    {
        public override Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!WithCompilerFinding) return Task.FromResult(Result(request, []));
            var finding = new ReviewFinding(
                "roslyn-cs1002-fixture",
                "analyzer",
                FindingSeverity.High,
                ".NET CS1002: ; expected",
                "; expected",
                "Correct the compiler diagnostic.",
                [new FindingLocation("Sample.cs", new FindingRange(
                    new FindingPosition(1, 1), new FindingPosition(1, 1)))],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "CS1002",
                Source: new FindingSource(FindingSourceKind.Deterministic, "roslyn", ".NET build", "10.0.301"));
            return Task.FromResult(Result(request, [finding]));
        }
    }

    private sealed class CapturingReviewExecutorFactory : IReviewExecutorFactory, IReviewExecutor
    {
        public TaskCompletionSource<ReviewRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReviewExecutor Create(
            string cliType,
            string? model,
            string? thinkingLevel,
            Action<string, CliRunEvent> eventObserver,
            Action<ReviewUsageEntry> usageRecorded) => this;

        public Task<ReviewExecutionResult> ReviewIfNeededAsync(
            ReviewRequest request,
            bool force,
            CancellationToken cancellationToken)
        {
            Request.TrySetResult(request);
            return Task.FromResult(new ReviewExecutionResult(false, null));
        }
    }
}
