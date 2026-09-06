using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Proves the single-port contract the container image relies on: the published browser bundle is
/// served by the API host itself, client routes fall back to the shell, and API routes never do.
/// </summary>
public sealed class StaticUiHostingTests : IAsyncLifetime
{
    private const string ShellMarker = "<!-- quality-studio-shell -->";
    private const string FingerprintedAsset = "main-ABCD1234.js";
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-ui-tests",
        Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "repository");
    private string HostRoot => Path.Combine(testRoot, "host");
    private UiApplication? application;

    [Fact]
    public async Task Root_serves_the_published_shell()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(ShellMarker, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task Fingerprinted_assets_are_served_immutable_and_plain_assets_are_not()
    {
        using var client = CreateClient();

        using var fingerprinted = await client.GetAsync("/" + FingerprintedAsset, TestContext.Current.CancellationToken);
        using var plain = await client.GetAsync("/favicon.svg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, fingerprinted.StatusCode);
        Assert.Equal("export const marker = 1;",
            await fingerprinted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromDays(365), fingerprinted.Headers.CacheControl?.MaxAge);
        Assert.Contains(fingerprinted.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(TimeSpan.FromHours(1), plain.Headers.CacheControl?.MaxAge);
        Assert.DoesNotContain(plain.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
    }

    [Fact]
    public async Task Client_route_deep_link_falls_back_to_the_shell()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/repos/default/file/src/Sample.cs");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ShellMarker, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_and_missing_assets_are_never_answered_with_the_shell()
    {
        using var client = CreateClient();
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/does-not-exist");
        apiRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var assetRequest = new HttpRequestMessage(HttpMethod.Get, "/missing-BADCAFE1.js");
        assetRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        using var api = await client.SendAsync(apiRequest, TestContext.Current.CancellationToken);
        using var asset = await client.SendAsync(assetRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.DoesNotContain(ShellMarker, await api.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
    }

    [Fact]
    public async Task The_api_still_answers_next_to_the_bundle()
    {
        using var client = CreateClient();

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        using var repositories = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repositories.StatusCode);
    }

    [Fact]
    public void A_host_without_a_bundle_resolves_no_ui_root()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Null(StaticUiHosting.ResolveRoot(configuration, new StubEnvironment(RepositoryRoot)));
        Assert.Equal(Path.Combine(HostRoot, StaticUiHosting.DefaultDirectoryName),
            StaticUiHosting.ResolveRoot(configuration, new StubEnvironment(HostRoot)));
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RepositoryRoot);
        var bundle = Path.Combine(HostRoot, StaticUiHosting.DefaultDirectoryName);
        Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.cs"), "public class Sample { }");
        await RunGitAsync(RepositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"),
            $"<!doctype html><html><head><title>Quality Studio</title></head><body>{ShellMarker}</body></html>");
        await File.WriteAllTextAsync(Path.Combine(bundle, FingerprintedAsset), "export const marker = 1;");
        await File.WriteAllTextAsync(Path.Combine(bundle, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        application = new UiApplication(RepositoryRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { TemporaryDirectory.Delete(testRoot); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private HttpClient CreateClient() => application!.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
            })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class StubEnvironment(string contentRoot) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "QualityStudio.Api";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class UiApplication(string root, string contentRoot) : WebApplicationFactory<Program>
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
                }));
        }
    }
}
