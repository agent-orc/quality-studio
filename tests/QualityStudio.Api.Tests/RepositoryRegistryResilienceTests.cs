using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryResilienceTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-registry-resilience", Guid.NewGuid().ToString("N"));
    private readonly RecordingLoggerProvider logs = new();
    private ResilienceApplication? application;

    [Fact]
    public async Task Poisoned_persisted_entry_is_quarantined_without_preventing_boot()
    {
        var allowedRoot = Path.Combine(testRoot, "allowed");
        var secondAllowedRoot = Path.Combine(testRoot, "also-allowed");
        var repositoryRoot = Path.Combine(allowedRoot, "quality-studio");
        var poisonedRoot = Path.Combine(testRoot, "outside", "agent-studio");
        var hostRoot = Path.Combine(testRoot, "host");
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(secondAllowedRoot);
        Directory.CreateDirectory(poisonedRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Valid.cs"),
            "namespace Valid; public sealed class Marker;", TestContext.Current.CancellationToken);
        await RunGitAsync(repositoryRoot);
        await RunGitAsync(poisonedRoot);
        WriteRegistry(hostRoot,
            new RepositoryRegistration("default", "Quality Studio", repositoryRoot, null, 12000,
                ["code", "security", "performance"], Blocked: true, BlockedReason: "stale state"),
            new RepositoryRegistration("agent-studio", "Agent Studio", poisonedRoot, null, 12000,
                ["code", "security", "performance"]));
        application = new ResilienceApplication(repositoryRoot, allowedRoot, secondAllowedRoot, hostRoot, logs);
        using var client = application.CreateClient();

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        using var registry = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, registry.StatusCode);
        var payload = await registry.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("default", payload.GetProperty("defaultRepositoryId").GetString());
        var repositories = payload.GetProperty("repositories").EnumerateArray().ToArray();
        var valid = Assert.Single(repositories, entry => entry.GetProperty("id").GetString() == "default");
        Assert.False(valid.GetProperty("blocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, valid.GetProperty("blockedReason").ValueKind);
        var poisoned = Assert.Single(repositories,
            entry => entry.GetProperty("id").GetString() == "agent-studio");
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        var reason = poisoned.GetProperty("blockedReason").GetString();
        Assert.Contains(poisonedRoot, reason, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, reason, StringComparison.Ordinal);
        Assert.Contains(secondAllowedRoot, reason, StringComparison.Ordinal);

        using var validTree = await client.GetAsync("/api/repos/default/tree?path=",
            TestContext.Current.CancellationToken);
        using var blockedTree = await client.GetAsync("/api/repos/agent-studio/tree?path=",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, validTree.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, blockedTree.StatusCode);

        var warning = Assert.Single(logs.Entries,
            entry => entry.Level == LogLevel.Warning && entry.EventId.Id == 1405);
        Assert.Contains(poisonedRoot, warning.Message, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, warning.Message, StringComparison.Ordinal);
        Assert.Contains(secondAllowedRoot, warning.Message, StringComparison.Ordinal);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        logs.Dispose();
        try
        {
            Directory.Delete(testRoot, true);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteRegistry(string hostRoot, params RepositoryRegistration[] entries)
    {
        var path = Path.Combine(hostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class ResilienceApplication(
        string repositoryRoot,
        string allowedRoot,
        string secondAllowedRoot,
        string contentRoot,
        RecordingLoggerProvider logs) : WebApplicationFactory<Program>
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

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
