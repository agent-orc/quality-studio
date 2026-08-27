using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CodingAgentRunner.Quota;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryQuarantineTests : IAsyncLifetime
{
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-hosts", Guid.NewGuid().ToString("N"));
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-repos", Guid.NewGuid().ToString("N"));
    private readonly string outOfRootDirectory = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-outside-" + Guid.NewGuid().ToString("N"));
    private TestApplication? application;

    [Fact]
    public async Task Boots_with_a_poisoned_registry_entry_quarantined_and_visible_while_the_in_root_entry_is_unaffected()
    {
        using var client = application!.CreateClient();

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var reposResponse = await client.GetAsync("/api/repos?includeArchived=false", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, reposResponse.StatusCode);
        var repos = await reposResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var entries = repos.GetProperty("repositories").EnumerateArray().ToArray();

        var goodEntry = Assert.Single(entries, entry => entry.GetProperty("id").GetString() == "default");
        Assert.False(goodEntry.GetProperty("blocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, goodEntry.GetProperty("blockReason").ValueKind);

        var quarantinedEntry = Assert.Single(entries, entry => entry.GetProperty("id").GetString() == "agent-studio");
        Assert.True(quarantinedEntry.GetProperty("blocked").GetBoolean());
        var reason = quarantinedEntry.GetProperty("blockReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(outOfRootDirectory, reason, StringComparison.Ordinal);
        Assert.Contains("allowed roots", reason, StringComparison.OrdinalIgnoreCase);

        using var goodTree = await client.GetAsync("/api/repos/default/tree?path=", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, goodTree.StatusCode);

        using var quarantinedTree = await client.GetAsync("/api/repos/agent-studio/tree?path=", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, quarantinedTree.StatusCode);
        var problem = await quarantinedTree.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Repository is quarantined and unavailable", problem.GetProperty("title").GetString());
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(outOfRootDirectory);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"), "namespace Sample; public static class Greeter { public static string Hello() => \"hello\"; }");
        await RunGitInDirectoryAsync(repositoryRoot, "init", "--quiet");

        var seeded = new List<RepositoryRegistration>
        {
            new("default", "Default repository", repositoryRoot, null, 12000, ["code", "security", "performance"]),
            new("agent-studio", "Agent Studio", outOfRootDirectory, null, 12000, ["code", "security", "performance"]),
        };
        var registryDirectory = Path.Combine(hostRoot, RepositoryRegistry.RelativeRegistryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(registryDirectory)!);
        await File.WriteAllTextAsync(registryDirectory,
            JsonSerializer.Serialize(seeded, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        application = new TestApplication(repositoryRoot, hostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        foreach (var directory in new[] { hostRoot, repositoryRoot, outOfRootDirectory })
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task RunGitInDirectoryAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TestApplication(string root, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = root,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
            });
        }
    }
}
