using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// The operating lids: one request may not return an unbounded file, an availability listing may not
/// start a process per sensor per call, and the dashboard cache may not grow with the commit history.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class OperatingLimitTests : IAsyncLifetime
{
    private const int FileLimitBytes = 8 * 1024;
    private const int PreviewBytes = 2 * 1024;
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-limit-tests",
        Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "repository");
    private string HostRoot => Path.Combine(testRoot, "host");
    private LimitedApplication? application;

    [Fact]
    public async Task A_small_file_is_returned_whole_and_reports_no_large_file_envelope()
    {
        using var client = application!.CreateClient();

        var response = await client.GetFromJsonAsync<JsonElement>("/api/file?path=Small.cs",
            TestContext.Current.CancellationToken);

        Assert.Equal("public class Small { }", response.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("largeFile").ValueKind);
    }

    [Fact]
    public async Task An_oversized_file_returns_a_capped_prefix_and_the_true_size()
    {
        using var client = application!.CreateClient();

        var response = await client.GetFromJsonAsync<JsonElement>("/api/file?path=Large.txt",
            TestContext.Current.CancellationToken);

        var largeFile = response.GetProperty("largeFile");
        var content = response.GetProperty("content").GetString()!;
        Assert.Equal(FileLimitBytes, largeFile.GetProperty("limitBytes").GetInt64());
        Assert.Equal(new FileInfo(Path.Combine(RepositoryRoot, "Large.txt")).Length,
            largeFile.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(largeFile.GetProperty("sizeBytes").GetInt64(), response.GetProperty("sizeBytes").GetInt64());
        Assert.True(largeFile.GetProperty("returnedBytes").GetInt64() <= PreviewBytes);
        Assert.Equal(largeFile.GetProperty("returnedBytes").GetInt64(), Encoding.UTF8.GetByteCount(content));
        // Cut on a character boundary, so the preview never ends in a replacement character.
        Assert.DoesNotContain('�', content);
        Assert.StartsWith("ä", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensor_availability_is_probed_once_per_ttl_rather_than_once_per_request()
    {
        var sensor = new CountingSensor();
        var cache = new SensorAvailabilityCache(Options.Create(new RepositoryOptions()));

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.ProbeAsync(sensor, TestContext.Current.CancellationToken)));
        await cache.ProbeAsync(sensor, TestContext.Current.CancellationToken);

        Assert.Equal(1, sensor.Probes);
    }

    [Fact]
    public async Task A_disabled_ttl_probes_every_time()
    {
        var sensor = new CountingSensor();
        var cache = new SensorAvailabilityCache(Options.Create(new RepositoryOptions
        {
            Limits = new LimitOptions { SensorAvailabilityCacheSeconds = 0 },
        }));

        await cache.ProbeAsync(sensor, TestContext.Current.CancellationToken);
        await cache.ProbeAsync(sensor, TestContext.Current.CancellationToken);

        Assert.Equal(2, sensor.Probes);
    }

    [Fact]
    public void The_dashboard_cache_drops_the_least_recently_used_projection()
    {
        var service = new ProjectDashboardService(Options.Create(new RepositoryOptions
        {
            Limits = new LimitOptions { ProjectDashboardCacheEntries = 3 },
        }));
        var roots = new RepositoryHierarchyCache().Get(RepositoryRoot).Roots;

        for (var commit = 0; commit < 20; commit++)
        {
            service.GetMeasured(RepositoryRoot,
                new RepositoryHierarchySnapshot(roots, $"state-{commit}", $"\"etag-{commit}\""));
        }

        Assert.Equal(3, service.CachedProjections);
    }

    [Fact]
    public async Task The_sensor_listing_stays_reachable_with_the_cache_in_front_of_it()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/sensors", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RepositoryRoot);
        Directory.CreateDirectory(HostRoot);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Small.cs"), "public class Small { }");
        // Two-byte characters throughout, so a naive byte cut would land inside one of them.
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Large.txt"),
            string.Concat(Enumerable.Repeat("ä", 32 * 1024)), new UTF8Encoding(false));
        await GitTestRepository.InitializeAsync(RepositoryRoot);
        application = new LimitedApplication(RepositoryRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { TemporaryDirectory.Delete(testRoot); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class CountingSensor : IReviewSensor
    {
        private int probes;

        public int Probes => probes;
        public string Id => "counting";
        public string Version => "1.0.0";
        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository];

        public async Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probes);
            await Task.Yield();
            return new SensorAvailability(true);
        }

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorScanResult(true, null, [],
                new SensorProvenance(Id, Version, "repository", ".", DateTime.UtcNow.ToString("O"),
                    new Dictionary<string, string>(StringComparer.Ordinal))));
    }

    private sealed class LimitedApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = root,
                    ["QualityStudio:Security:Mode"] = "Local",
                    ["QualityStudio:Limits:MaxFileBytes"] = FileLimitBytes.ToString(),
                    ["QualityStudio:Limits:LargeFilePreviewBytes"] = PreviewBytes.ToString(),
                }));
        }
    }
}
