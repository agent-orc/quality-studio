using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Regression coverage for the 2026-08-11 incident: a persisted registry entry that no longer
/// satisfies AllowedRoots must be quarantined at boot instead of crashing the host.
/// </summary>
public sealed class RepositoryRegistryBootResilienceTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-boot-resilience-tests", Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "in-root-repo");
    private string OutsideRoot => Path.Combine(testRoot, "outside", "poisoned-repo");
    private string ContentRoot => Path.Combine(testRoot, "host");
    private BootApplication? application;

    [Fact]
    public async Task Boots_with_a_poisoned_registry_entry_and_quarantines_it()
    {
        application = new BootApplication(RepositoryRoot, ContentRoot);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var repositories = body.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.Equal(2, repositories.Length);

        var poisoned = repositories.Single(entry => entry.GetProperty("id").GetString() == "poisoned");
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        var reason = poisoned.GetProperty("blockedReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(OutsideRoot, reason, StringComparison.Ordinal);
        Assert.Contains(RepositoryRoot, reason, StringComparison.Ordinal);

        var healthy = repositories.Single(entry => entry.GetProperty("id").GetString() == "default");
        Assert.False(healthy.GetProperty("blocked").GetBoolean());
        Assert.Null(healthy.GetProperty("blockedReason").GetString());

        using var quarantinedAccess = await client.GetAsync("/api/repos/poisoned/file?path=Sample.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, quarantinedAccess.StatusCode);

        using var healthyAccess = await client.GetAsync("/api/repos/default/file?path=Sample.cs",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthyAccess.StatusCode);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(RepositoryRoot);
        Directory.CreateDirectory(OutsideRoot);
        Directory.CreateDirectory(ContentRoot);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.cs"),
            "namespace Sample; public sealed class Subject;");
        WriteRegistry();
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private void WriteRegistry()
    {
        var path = Path.Combine(ContentRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration(RepositoryRegistry.DefaultRepositoryId, "Default", RepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("poisoned", "Poisoned", OutsideRoot, null, 12000,
                new[] { "code", "security", "performance" }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private sealed class BootApplication(string root, string contentRoot) : WebApplicationFactory<Program>
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
