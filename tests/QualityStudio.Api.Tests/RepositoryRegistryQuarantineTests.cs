using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// QS-77: a persisted registry entry outside the configured allowed roots must be quarantined,
/// not crash the API on boot (see docs/operations/security/index.html#decision-qs-77).
/// </summary>
public sealed class RepositoryRegistryQuarantineTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-tests", Guid.NewGuid().ToString("N"));
    private readonly ReviewMetaIndex metaIndex = new();

    public void Dispose()
    {
        metaIndex.Dispose();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    [Fact]
    public void Constructor_quarantines_an_out_of_root_persisted_entry_instead_of_throwing()
    {
        var allowedRoot = Path.Combine(testRoot, "allowed");
        var validRoot = Path.Combine(allowedRoot, "default");
        var poisonedRoot = Path.Combine(testRoot, "outside", "agent-studio");
        Directory.CreateDirectory(validRoot);
        Directory.CreateDirectory(poisonedRoot);

        var contentRoot = Path.Combine(testRoot, "host");
        Directory.CreateDirectory(contentRoot);
        WriteRegistry(contentRoot, [
            new RepositoryRegistration("default", "Default", validRoot, null, 12000, ["code", "security", "performance"]),
            new RepositoryRegistration("poisoned", "Poisoned", poisonedRoot, null, 12000, ["code", "security", "performance"]),
        ]);

        var logger = new CapturingLogger<RepositoryRegistry>();
        var options = Options.Create(new RepositoryOptions
        {
            RepositoryRoot = validRoot,
            AllowedRoots = [allowedRoot],
        });

        var registry = new RepositoryRegistry(new FakeHostEnvironment(contentRoot), options,
            new SensorRegistry([]), logger, metaIndex);

        // In-root entries are unaffected: the valid repository remains usable.
        var usable = registry.List();
        var defaultEntry = Assert.Single(usable);
        Assert.Equal("default", defaultEntry.Id);
        Assert.Equal(validRoot, defaultEntry.RootPath);
        Assert.Equal(validRoot, registry.Get("default").RootPath);

        // The out-of-root entry is quarantined and visible, not silently dropped.
        var quarantinedEntry = Assert.Single(registry.Quarantined);
        Assert.Equal("poisoned", quarantinedEntry.Id);
        Assert.Equal(poisonedRoot, quarantinedEntry.RootPath);
        Assert.Contains(poisonedRoot, quarantinedEntry.Reason, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, quarantinedEntry.Reason, StringComparison.Ordinal);
        Assert.Throws<KeyNotFoundException>(() => registry.Get("poisoned"));

        // The quarantine is logged as a warning naming the path and the allowed roots.
        var warning = Assert.Single(logger.Messages, message => message.Contains("Quarantined", StringComparison.Ordinal));
        Assert.Contains(poisonedRoot, warning, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_boots_with_a_poisoned_registry_entry_and_lists_it_as_quarantined()
    {
        var allowedRoot = Path.Combine(testRoot, "allowed");
        var validRoot = Path.Combine(allowedRoot, "default");
        var poisonedRoot = Path.Combine(testRoot, "outside", "agent-studio");
        Directory.CreateDirectory(validRoot);
        Directory.CreateDirectory(poisonedRoot);

        var contentRoot = Path.Combine(testRoot, "host");
        Directory.CreateDirectory(contentRoot);
        WriteRegistry(contentRoot, [
            new RepositoryRegistration("default", "Default", validRoot, null, 12000, ["code", "security", "performance"]),
            new RepositoryRegistration("poisoned", "Poisoned", poisonedRoot, null, 12000, ["code", "security", "performance"]),
        ]);

        await using var application = new PoisonedRegistryApplication(allowedRoot, validRoot, contentRoot);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var repos = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var repositories = repos.GetProperty("repositories").EnumerateArray().ToArray();
        var defaultRepository = Assert.Single(repositories);
        Assert.Equal("default", defaultRepository.GetProperty("id").GetString());

        var quarantined = repos.GetProperty("quarantined").EnumerateArray().ToArray();
        var poisoned = Assert.Single(quarantined);
        Assert.Equal("poisoned", poisoned.GetProperty("id").GetString());
        Assert.Contains(poisonedRoot, poisoned.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains(allowedRoot, poisoned.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    private static void WriteRegistry(string contentRoot, RepositoryRegistration[] entries)
    {
        var path = Path.Combine(contentRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class PoisonedRegistryApplication(string allowedRoot, string repositoryRoot, string contentRoot)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = repositoryRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }
}
