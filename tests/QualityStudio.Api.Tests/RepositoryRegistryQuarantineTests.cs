using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryQuarantineTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-registry-tests", Guid.NewGuid().ToString("N"));

    public RepositoryRegistryQuarantineTests()
    {
        Directory.CreateDirectory(AllowedRoot);
        Directory.CreateDirectory(OutOfRootPath);
    }

    private string AllowedRoot => Path.Combine(testRoot, "allowed");
    private string OutOfRootPath => Path.Combine(testRoot, "outside");

    [Fact]
    public void An_out_of_root_persisted_entry_is_quarantined_and_an_in_root_entry_is_unaffected()
    {
        WriteRegistry(new[]
        {
            new RepositoryRegistration("default", "Default", AllowedRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("poisoned", "Poisoned", OutOfRootPath, null, 12000,
                new[] { "code", "security", "performance" }),
        });

        var logger = new RecordingLogger<RepositoryRegistry>();
        var registry = CreateRegistry(logger);

        var entries = registry.List(includeBlocked: true);
        var defaultEntry = Assert.Single(entries, entry => entry.Id == "default");
        Assert.False(defaultEntry.Blocked);

        var poisoned = Assert.Single(entries, entry => entry.Id == "poisoned");
        Assert.True(poisoned.Blocked);
        Assert.False(string.IsNullOrWhiteSpace(poisoned.BlockedReason));

        Assert.Throws<KeyNotFoundException>(() => registry.Get("poisoned"));
        Assert.Same(poisoned, registry.Get("poisoned", includeBlocked: true));

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(OutOfRootPath, warning.Message, StringComparison.Ordinal);
        Assert.Contains(AllowedRoot, warning.Message, StringComparison.Ordinal);
    }

    private RepositoryRegistry CreateRegistry(ILogger<RepositoryRegistry> logger)
    {
        var environment = new FakeHostEnvironment(testRoot);
        var options = Options.Create(new RepositoryOptions
        {
            RepositoryRoot = AllowedRoot,
            AllowedRoots = [AllowedRoot],
        });
        var sensors = new SensorRegistry([]);
        return new RepositoryRegistry(environment, options, sensors, logger, new ReviewMetaIndex());
    }

    private void WriteRegistry(RepositoryRegistration[] entries)
    {
        var path = Path.Combine(testRoot, RepositoryRegistry.RelativeRegistryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    public void Dispose()
    {
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
