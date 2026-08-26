using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace QualityStudio.Api.Tests;

[CollectionDefinition("Repository registry resilience", DisableParallelization = true)]
public sealed class RepositoryRegistryResilienceCollection
{
}

[Collection("Repository registry resilience")]
public sealed class RepositoryRegistryResilienceTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-registry-resilience", Guid.NewGuid().ToString("N"));
    private string AllowedRoot => Path.Combine(testRoot, "allowed");
    private string SecondAllowedRoot => Path.Combine(testRoot, "also-allowed");
    private string RepositoryRoot => Path.Combine(AllowedRoot, "repository");
    private string PoisonedRoot => Path.Combine(testRoot, "poisoned");
    private string HostRoot => Path.Combine(testRoot, "host");

    [Fact]
    public async Task Poisoned_persisted_entry_is_quarantined_without_preventing_boot()
    {
        var logs = new CapturingLoggerProvider();
        await using var application = new TestApplication(
            RepositoryRoot, AllowedRoot, SecondAllowedRoot, HostRoot, logs);
        using var client = application.CreateClient();

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var payload = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var repositories = payload.GetProperty("repositories").EnumerateArray().ToArray();
        var valid = Assert.Single(repositories, entry => entry.GetProperty("id").GetString() == "default");
        var poisoned = Assert.Single(repositories, entry => entry.GetProperty("id").GetString() == "poisoned");

        Assert.False(valid.GetProperty("blocked").GetBoolean());
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        Assert.Contains(PoisonedRoot, poisoned.GetProperty("blockedReason").GetString(), StringComparison.Ordinal);
        Assert.Contains(AllowedRoot, poisoned.GetProperty("blockedReason").GetString(), StringComparison.Ordinal);
        Assert.Contains(SecondAllowedRoot, poisoned.GetProperty("blockedReason").GetString(), StringComparison.Ordinal);

        using var validFile = await client.GetAsync(
            "/api/repos/default/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, validFile.StatusCode);

        using var blockedFile = await client.GetAsync(
            "/api/repos/poisoned/file?path=Poisoned.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, blockedFile.StatusCode);

        var registry = application.Services.GetRequiredService<RepositoryRegistry>();
        Assert.Equal("default", Assert.Single(registry.List()).Id);
        var error = await Assert.ThrowsAsync<RepositoryRegistryValidationException>(() => registry.CreateAsync(
            new RepositoryRegistrationRequest("another-poison", "Another poison", PoisonedRoot, null, null, ["code"]),
            TestContext.Current.CancellationToken));
        Assert.Contains(PoisonedRoot, error.Message, StringComparison.Ordinal);
        Assert.Contains(AllowedRoot, error.Message, StringComparison.Ordinal);
        Assert.Contains(SecondAllowedRoot, error.Message, StringComparison.Ordinal);

        var warning = Assert.Single(logs.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.EventId.Name == "RepositoryQuarantined");
        Assert.Contains(PoisonedRoot, warning.Message, StringComparison.Ordinal);
        Assert.Contains(AllowedRoot, warning.Message, StringComparison.Ordinal);
        Assert.Contains(SecondAllowedRoot, warning.Message, StringComparison.Ordinal);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RepositoryRoot);
        Directory.CreateDirectory(SecondAllowedRoot);
        Directory.CreateDirectory(PoisonedRoot);
        Directory.CreateDirectory(HostRoot);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.cs"),
            "namespace Sample; public sealed class Subject;");
        await File.WriteAllTextAsync(Path.Combine(PoisonedRoot, "Poisoned.cs"),
            "namespace Poisoned; public sealed class Subject;");
        await RunGitAsync(RepositoryRoot);
        await RunGitAsync(PoisonedRoot);

        var registryPath = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", RepositoryRoot, null, 12_000,
                ["code", "security", "performance"], Blocked: true, BlockedReason: "stale quarantine"),
            new RepositoryRegistration("poisoned", "Poisoned", PoisonedRoot, null, 12_000,
                ["code", "security", "performance"]),
        };
        await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TestApplication(
        string repositoryRoot,
        string allowedRoot,
        string secondAllowedRoot,
        string contentRoot,
        CapturingLoggerProvider logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["QualityStudio:AllowedRoots:1"] = secondAllowedRoot,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }

    private sealed record CapturedLog(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<CapturedLog> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new CapturedLog(logLevel, eventId, formatter(state, exception)));
        }
    }
}
