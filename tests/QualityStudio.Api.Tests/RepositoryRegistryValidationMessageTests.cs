using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Direct unit coverage that EnsureAllowedDirectory names the offending path and the allowed roots.</summary>
public sealed class RepositoryRegistryValidationMessageTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-registry-message-tests", Guid.NewGuid().ToString("N"));
    private readonly ReviewMetaIndex metaIndex = new();

    public RepositoryRegistryValidationMessageTests()
    {
        Directory.CreateDirectory(testRoot);
    }

    [Fact]
    public async Task Onboarding_outside_the_allowed_roots_names_the_path_and_the_roots()
    {
        var allowedRoot = Path.Combine(testRoot, "allowed");
        var outsidePath = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(allowedRoot);
        Directory.CreateDirectory(outsidePath);

        var registry = CreateRegistry(allowedRoot);
        var exception = await Assert.ThrowsAsync<RepositoryRegistryValidationException>(() =>
            registry.CreateAsync(new RepositoryRegistrationRequest(null, "Outside", outsidePath, null, null, null),
                CancellationToken.None));

        Assert.Contains(Path.GetFullPath(outsidePath), exception.Message, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(allowedRoot), exception.Message, StringComparison.Ordinal);
        Assert.Equal("Repository path is outside the allowed roots", exception.PublicTitle);
    }

    public void Dispose()
    {
        metaIndex.Dispose();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private RepositoryRegistry CreateRegistry(string allowedRoot) => new(
        new FakeHostEnvironment(allowedRoot),
        Options.Create(new RepositoryOptions
        {
            RepositoryRoot = allowedRoot,
            AllowedRoots = [allowedRoot],
        }),
        new SensorRegistry(Array.Empty<IReviewSensor>()),
        NullLogger<RepositoryRegistry>.Instance,
        metaIndex);

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "QualityStudio.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
