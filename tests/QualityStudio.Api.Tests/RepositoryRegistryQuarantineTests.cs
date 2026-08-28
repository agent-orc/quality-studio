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
    private string AllowedRepositoryRoot => Path.Combine(testRoot, "allowed");
    private string PoisonedRepositoryRoot => Path.Combine(testRoot, "poisoned-outside-root");
    private string HostRoot => Path.Combine(testRoot, "host");
    private TestApplication? application;

    [Fact]
    public async Task Boot_succeeds_quarantines_the_poisoned_entry_and_leaves_the_healthy_entry_usable()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var repositories = json.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.Equal(2, repositories.Length);

        var healthy = Assert.Single(repositories, repo => repo.GetProperty("id").GetString() == "default");
        Assert.False(healthy.GetProperty("blocked").GetBoolean());
        Assert.True(string.IsNullOrEmpty(healthy.GetProperty("blockedReason").GetString()));

        var poisoned = Assert.Single(repositories, repo => repo.GetProperty("id").GetString() == "poisoned");
        Assert.True(poisoned.GetProperty("blocked").GetBoolean());
        var reason = poisoned.GetProperty("blockedReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(PoisonedRepositoryRoot, reason, StringComparison.Ordinal);
        Assert.Contains(AllowedRepositoryRoot, reason, StringComparison.Ordinal);

        using var blocked = await client.GetAsync("/api/repos/poisoned/handover", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Repository is quarantined", problem.GetProperty("title").GetString());

        using var handover = await client.GetAsync("/api/repos/default/handover", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, handover.StatusCode);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(AllowedRepositoryRoot);
        Directory.CreateDirectory(PoisonedRepositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(AllowedRepositoryRoot, "Sample.cs"), "namespace Sample; public sealed class Subject;");
        await RunGitAsync(AllowedRepositoryRoot);
        WriteRegistry();
        application = new TestApplication(AllowedRepositoryRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private void WriteRegistry()
    {
        var path = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", AllowedRepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("poisoned", "Poisoned", PoisonedRepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
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

    private sealed class TestApplication(string allowedRoot, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = allowedRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                }));
        }
    }
}
