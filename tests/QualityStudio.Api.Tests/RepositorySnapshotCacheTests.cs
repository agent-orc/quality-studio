using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositorySnapshotCacheTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "quality-studio-snapshot-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-snapshot-host-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Persisted_snapshot_restores_and_invalidates_on_registry_or_worktree_change()
    {
        var registration = Registration();
        var firstHierarchy = new RepositoryHierarchyCache();
        var firstDashboards = new ProjectDashboardService();
        var firstStore = Store(firstHierarchy, firstDashboards);
        var snapshot = firstHierarchy.Get(root);
        var dashboard = firstDashboards.Get(root, snapshot);
        await firstStore.SaveAsync(
            registration,
            snapshot,
            dashboard,
            new Dictionary<string, SensorAvailability>(),
            TestContext.Current.CancellationToken);

        var restoredHierarchy = new RepositoryHierarchyCache();
        var restoredDashboards = new ProjectDashboardService();
        var restoredStore = Store(restoredHierarchy, restoredDashboards);
        Assert.True(await restoredStore.TryRestoreAsync(
            registration, null, TestContext.Current.CancellationToken));
        Assert.True(restoredHierarchy.GetMeasured(root).CacheHit);
        Assert.True(restoredDashboards.GetMeasured(root, restoredHierarchy.Get(root)).CacheHit);

        var changedRegistration = registration with { DisplayName = "Renamed repository" };
        var registryChangedStore = Store(new RepositoryHierarchyCache(), new ProjectDashboardService());
        Assert.False(await registryChangedStore.TryRestoreAsync(
            changedRegistration, null, TestContext.Current.CancellationToken));

        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.cs"),
            "namespace Sample; public sealed class Changed;",
            TestContext.Current.CancellationToken);
        var worktreeChangedStore = Store(new RepositoryHierarchyCache(), new ProjectDashboardService());
        Assert.False(await worktreeChangedStore.TryRestoreAsync(
            registration, null, TestContext.Current.CancellationToken));

        RunGit("add", "Sample.cs");
        RunGit("commit", "--quiet", "-m", "Change fixture HEAD");
        var headChangedStore = Store(new RepositoryHierarchyCache(), new ProjectDashboardService());
        Assert.False(await headChangedStore.TryRestoreAsync(
            registration, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Corrupt_persisted_snapshot_falls_back_to_cold_rebuild()
    {
        var cachePath = Path.Combine(hostRoot, ".quality-studio", "cache", "repositories");
        Directory.CreateDirectory(cachePath);
        await File.WriteAllTextAsync(
            Path.Combine(cachePath, "default.json"),
            "{not-json",
            TestContext.Current.CancellationToken);

        var hierarchy = new RepositoryHierarchyCache();
        var dashboards = new ProjectDashboardService();
        var store = Store(hierarchy, dashboards);
        Assert.False(await store.TryRestoreAsync(
            Registration(), null, TestContext.Current.CancellationToken));
        Assert.False(hierarchy.GetMeasured(root).CacheHit);
    }

    [Fact]
    public async Task Sensor_availability_is_cached_by_repository_state_and_registry_entry()
    {
        var hierarchy = new RepositoryHierarchyCache();
        var cache = new RepositorySensorAvailabilityCache(hierarchy);
        var sensor = new CountingSensor();
        var sensors = new SensorRegistry([sensor]);
        var registration = Registration() with
        {
            Sensors = [new RepositorySensorConfiguration(sensor.Id)],
        };

        var first = await cache.GetAsync(registration, sensors, TestContext.Current.CancellationToken);
        var warm = await cache.GetAsync(registration, sensors, TestContext.Current.CancellationToken);
        Assert.False(first.CacheHit);
        Assert.True(warm.CacheHit);
        Assert.Equal(1, sensor.Probes);

        var registryChanged = await cache.GetAsync(
            registration with { DisplayName = "Renamed repository" },
            sensors,
            TestContext.Current.CancellationToken);
        Assert.False(registryChanged.CacheHit);
        Assert.Equal(2, sensor.Probes);

        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.cs"),
            "namespace Sample; public sealed class Changed;",
            TestContext.Current.CancellationToken);
        var worktreeChanged = await cache.GetAsync(
            registration with { DisplayName = "Renamed repository" },
            sensors,
            TestContext.Current.CancellationToken);
        Assert.False(worktreeChanged.CacheHit);
        Assert.Equal(3, sensor.Probes);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.cs"),
            "namespace Sample; public sealed class Initial;");
        RunGit("init", "--quiet");
        RunGit("config", "user.email", "snapshot-tests@example.invalid");
        RunGit("config", "user.name", "Snapshot tests");
        RunGit("add", ".");
        RunGit("commit", "--quiet", "-m", "Initial fixture");
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(root, true);
        Directory.Delete(hostRoot, true);
        return ValueTask.CompletedTask;
    }

    private RepositoryRegistration Registration() => new(
        "default",
        "Snapshot fixture",
        root,
        null,
        InputResolver.DefaultBudgetCharacters,
        ["code", "security", "performance"],
        []);

    private RepositorySnapshotStore Store(
        RepositoryHierarchyCache hierarchy,
        ProjectDashboardService dashboards) => new(
        new TestHostEnvironment(hostRoot),
        hierarchy,
        dashboards,
        new RepositorySensorAvailabilityCache(hierarchy),
        NullLogger<RepositorySnapshotStore>.Instance);

    private void RunGit(params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class CountingSensor : IReviewSensor
    {
        public string Id => "counting";
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];
        public int Probes { get; private set; }

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            Probes++;
            return Task.FromResult(new SensorAvailability(true));
        }

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
