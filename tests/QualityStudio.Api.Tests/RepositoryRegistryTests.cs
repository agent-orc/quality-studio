using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Covers the QS-77 boot-resilience fix: a persisted repository entry that no longer validates
/// (root removed, or outside the currently configured AllowedRoots) must be quarantined instead of
/// crashing RepositoryRegistry's constructor during host startup.</summary>
public sealed class RepositoryRegistryTests : IDisposable
{
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-registry-tests", Guid.NewGuid().ToString("N"));
    private readonly string allowedRoot;
    private readonly string healthyRepositoryRoot;
    private readonly string outsideRoot;

    public RepositoryRegistryTests()
    {
        Directory.CreateDirectory(hostRoot);
        allowedRoot = Path.Combine(hostRoot, "allowed");
        healthyRepositoryRoot = Path.Combine(allowedRoot, "healthy-repo");
        Directory.CreateDirectory(healthyRepositoryRoot);
        outsideRoot = Path.Combine(Path.GetTempPath(), "quality-studio-registry-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
    }

    [Fact]
    public void Constructor_quarantines_an_out_of_root_persisted_entry_without_throwing()
    {
        SeedRegistry(
            new RepositoryRegistration("healthy", "Healthy repo", healthyRepositoryRoot, null, 12000, ["code"]),
            new RepositoryRegistration("poisoned", "Poisoned repo", outsideRoot, null, 12000, ["code"]));
        var logger = new CapturingLogger<RepositoryRegistry>();

        var registry = CreateRegistry(logger);

        var entries = registry.List();
        Assert.Equal(2, entries.Count);

        var poisoned = Assert.Single(entries, entry => entry.Id == "poisoned");
        Assert.True(poisoned.Blocked);
        Assert.NotNull(poisoned.BlockedReason);
        var reason = poisoned.BlockedReason!;
        Assert.Contains(outsideRoot, reason, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, reason, StringComparison.Ordinal);

        Assert.Contains(logger.Warnings, message =>
            message.Contains(outsideRoot, StringComparison.Ordinal) && message.Contains(allowedRoot, StringComparison.Ordinal));
    }

    [Fact]
    public void Constructor_leaves_in_root_entries_unaffected_and_usable()
    {
        SeedRegistry(
            new RepositoryRegistration("healthy", "Healthy repo", healthyRepositoryRoot, null, 12000, ["code"]),
            new RepositoryRegistration("poisoned", "Poisoned repo", outsideRoot, null, 12000, ["code"]));

        var registry = CreateRegistry(new CapturingLogger<RepositoryRegistry>());

        var healthy = registry.Get("healthy");
        Assert.False(healthy.Blocked);
        Assert.Null(healthy.BlockedReason);
        Assert.Equal(healthyRepositoryRoot, healthy.RootPath);
    }

    [Fact]
    public void Get_excludes_a_quarantined_entry_from_normal_use()
    {
        SeedRegistry(
            new RepositoryRegistration("healthy", "Healthy repo", healthyRepositoryRoot, null, 12000, ["code"]),
            new RepositoryRegistration("poisoned", "Poisoned repo", outsideRoot, null, 12000, ["code"]));

        var registry = CreateRegistry(new CapturingLogger<RepositoryRegistry>());

        Assert.Throws<KeyNotFoundException>(() => registry.Get("poisoned"));
        var stillVisible = registry.Get("poisoned", includeBlocked: true);
        Assert.True(stillVisible.Blocked);
    }

    [Fact]
    public void EnsureAllowedDirectory_violation_message_names_the_path_and_allowed_roots()
    {
        SeedRegistry(new RepositoryRegistration("healthy", "Healthy repo", healthyRepositoryRoot, null, 12000, ["code"]));
        var registry = CreateRegistry(new CapturingLogger<RepositoryRegistry>());

        var exception = Assert.Throws<RepositoryRegistryValidationException>(() =>
            registry.CreateAsync(new RepositoryRegistrationRequest(
                null, "Out of root", outsideRoot, null, null, null), TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Contains(outsideRoot, exception.Message, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, exception.Message, StringComparison.Ordinal);
    }

    private void SeedRegistry(params RepositoryRegistration[] entries)
    {
        var directory = Path.Combine(hostRoot, ".quality-studio");
        Directory.CreateDirectory(directory);
        var registryPath = Path.Combine(directory, "repositories.json");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        File.WriteAllText(registryPath, JsonSerializer.Serialize(entries, options));
    }

    private RepositoryRegistry CreateRegistry(ILogger<RepositoryRegistry> logger) => new(
        new FakeHostEnvironment(hostRoot),
        Options.Create(new RepositoryOptions
        {
            RepositoryRoot = allowedRoot,
            AllowedRoots = [allowedRoot],
        }),
        new SensorRegistry([]),
        logger,
        new ReviewMetaIndex());

    public void Dispose()
    {
        if (Directory.Exists(hostRoot)) Directory.Delete(hostRoot, true);
        if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, true);
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}
