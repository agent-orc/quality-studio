using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Covers the QS-77 boot-resilience fix: a persisted repository registry entry that
/// fails allowed-root validation must quarantine that entry, not crash the API host.</summary>
public sealed class RepositoryRegistryBootTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-boot-tests", Guid.NewGuid().ToString("N"));
    private string HostRoot => Path.Combine(testRoot, "host");
    private string DefaultRepositoryRoot => Path.Combine(testRoot, "default-repo");
    private string OutsideRepositoryRoot => Path.Combine(testRoot, "outside", "poisoned-repo");
    private readonly CapturingLoggerProvider logs = new();
    private BootApplication? application;

    public async ValueTask InitializeAsync()
    {
        foreach (var directory in new[] { HostRoot, DefaultRepositoryRoot, OutsideRepositoryRoot })
        {
            Directory.CreateDirectory(directory);
        }
        await File.WriteAllTextAsync(Path.Combine(DefaultRepositoryRoot, "Sample.cs"),
            "namespace Sample; public sealed class Subject;");

        var registryPath = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", DefaultRepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("poisoned", "Poisoned", OutsideRepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
        };
        await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        application = new BootApplication(DefaultRepositoryRoot, HostRoot, logs);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Boot_succeeds_and_quarantines_only_the_out_of_root_entry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Constructing the client forces the host (and RepositoryRegistry) to build. It must not throw.
        using var client = application!.CreateClient();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/repos", cancellationToken);
        var repositories = list.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.Equal(2, repositories.Length);

        var poisoned = repositories.Single(repository => repository.GetProperty("id").GetString() == "poisoned");
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        var reason = poisoned.GetProperty("blockReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(OutsideRepositoryRoot, reason, StringComparison.Ordinal);
        Assert.Contains(DefaultRepositoryRoot, reason, StringComparison.Ordinal);

        var defaultRepository = repositories.Single(repository => repository.GetProperty("id").GetString() == "default");
        Assert.False(defaultRepository.GetProperty("blocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, defaultRepository.GetProperty("blockReason").ValueKind);
    }

    [Fact]
    public async Task Quarantined_repository_is_excluded_from_use()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = application!.CreateClient();

        using var poisonedFile = await client.GetAsync(
            "/api/repos/poisoned/file?path=Sample.cs", cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, poisonedFile.StatusCode);

        using var defaultFile = await client.GetAsync(
            "/api/repos/default/file?path=Sample.cs", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, defaultFile.StatusCode);
    }

    [Fact]
    public async Task Quarantine_warning_names_the_offending_path_and_the_allowed_roots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = application!.CreateClient();
        await client.GetAsync("/health", cancellationToken);

        var warning = Assert.Single(logs.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("poisoned", StringComparison.Ordinal));
        Assert.Contains(OutsideRepositoryRoot, warning.Message, StringComparison.Ordinal);
        Assert.Contains(DefaultRepositoryRoot, warning.Message, StringComparison.Ordinal);
    }

    private sealed class BootApplication(string allowedRoot, string contentRoot, CapturingLoggerProvider logs)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = allowedRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public readonly List<LogEntry> Entries = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
