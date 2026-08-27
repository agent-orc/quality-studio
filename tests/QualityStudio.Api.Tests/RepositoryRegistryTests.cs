using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryTests
{
    private static readonly string[] KnownSensorIds = ["roslyn", "tsc", "eslint", "sarif", "gitleaks"];

    [Fact]
    public void Roslyn_and_tsc_default_enabled_and_configured_when_repository_shape_supports_them()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-registry-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Sample.slnx"), "<Solution />");
            Directory.CreateDirectory(Path.Combine(root, "frontend"));
            File.WriteAllText(Path.Combine(root, "frontend", "tsconfig.app.json"), "{}");

            var repository = CreateRegistry(root).Get(RepositoryRegistry.DefaultRepositoryId);
            var sensors = repository.Sensors!.ToDictionary(sensor => sensor.Id, StringComparer.Ordinal);

            Assert.True(sensors["roslyn"].Enabled);
            Assert.Equal(".quality/analyzers/roslyn.sarif", sensors["roslyn"].Configuration!["reportPath"]);
            Assert.Contains("{reportPath}", sensors["roslyn"].Configuration!["command"]);

            Assert.True(sensors["tsc"].Enabled);
            Assert.Equal("frontend", sensors["tsc"].Configuration!["workingDirectory"]);
            Assert.Contains("tsconfig.app.json", sensors["tsc"].Configuration!["command"]);
            Assert.Equal(".quality/analyzers/tsc.txt", sensors["tsc"].Configuration!["reportPath"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Roslyn_and_tsc_default_disabled_when_repository_shape_is_absent()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-registry-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), "no solution or tsconfig here");

            var repository = CreateRegistry(root).Get(RepositoryRegistry.DefaultRepositoryId);
            var sensors = repository.Sensors!.ToDictionary(sensor => sensor.Id, StringComparer.Ordinal);

            Assert.False(sensors["roslyn"].Enabled);
            Assert.Null(sensors["roslyn"].Configuration);
            Assert.False(sensors["tsc"].Enabled);
            Assert.Null(sensors["tsc"].Configuration);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Eslint_and_generic_sarif_sensors_default_disabled_until_pinned()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-registry-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Sample.slnx"), "<Solution />");

            var repository = CreateRegistry(root).Get(RepositoryRegistry.DefaultRepositoryId);
            var sensors = repository.Sensors!.ToDictionary(sensor => sensor.Id, StringComparer.Ordinal);

            Assert.False(sensors["eslint"].Enabled);
            Assert.False(sensors["sarif"].Enabled);
            Assert.True(sensors["gitleaks"].Enabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static RepositoryRegistry CreateRegistry(string root)
    {
        var environment = new FakeHostEnvironment { ContentRootPath = root };
        var options = Options.Create(new RepositoryOptions
        {
            RepositoryRoot = ".",
            AllowedRoots = ["."],
        });
        var sensorRegistry = new SensorRegistry(KnownSensorIds.Select(id => (IReviewSensor)new FakeSensor(id)));
        return new RepositoryRegistry(
            environment, options, sensorRegistry, NullLogger<RepositoryRegistry>.Instance, new ReviewMetaIndex());
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeSensor(string id) : IReviewSensor
    {
        public string Id { get; } = id;
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true));

        public Task<SensorScanResult> RunAsync(
            SensorScanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
