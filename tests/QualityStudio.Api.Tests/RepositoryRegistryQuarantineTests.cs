using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryQuarantineTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-tests", Guid.NewGuid().ToString("N"));
    private string InRoot => Path.Combine(testRoot, "in-root");
    private string OutsideRoot => Path.Combine(testRoot, "outside-root");
    private string HostRoot => Path.Combine(testRoot, "host");
    private QuarantineApplication? application;

    [Fact]
    public async Task Boots_with_a_poisoned_registry_entry_and_quarantines_it_while_the_in_root_entry_stays_usable()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var repositories = json.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.Equal(2, repositories.Length);

        var healthy = Assert.Single(repositories, repo => repo.GetProperty("id").GetString() == "in-root");
        Assert.False(healthy.GetProperty("blocked").GetBoolean());

        var poisoned = Assert.Single(repositories, repo => repo.GetProperty("id").GetString() == "outside-root");
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        var reason = poisoned.GetProperty("blockedReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(OutsideRoot, reason, StringComparison.Ordinal);
        Assert.Contains(InRoot, reason, StringComparison.Ordinal);

        using var healthyFile = await client.GetAsync("/api/repos/in-root/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthyFile.StatusCode);

        using var blockedFile = await client.GetAsync("/api/repos/outside-root/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blockedFile.StatusCode);
        var problem = await blockedFile.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Repository quarantined", problem.GetProperty("title").GetString());
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(InRoot);
        Directory.CreateDirectory(OutsideRoot);
        Directory.CreateDirectory(HostRoot);
        await File.WriteAllTextAsync(Path.Combine(InRoot, "Sample.cs"), "namespace Sample; public sealed class Marker;");
        await RunGitAsync(InRoot);
        await File.WriteAllTextAsync(Path.Combine(OutsideRoot, "Sample.cs"), "namespace Sample; public sealed class Marker;");
        await RunGitAsync(OutsideRoot);

        var registryPath = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        var entries = new[]
        {
            new RepositoryRegistration("in-root", "In root", InRoot, null, 12000, new[] { "code" }),
            new RepositoryRegistration("outside-root", "Outside root", OutsideRoot, null, 12000, new[] { "code" }),
        };
        await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        application = new QuarantineApplication(InRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        try
        {
            Directory.Delete(testRoot, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task RunGitAsync(string directory)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init --quiet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class QuarantineApplication(string allowedRoot, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = allowedRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                }));
        }
    }
}
