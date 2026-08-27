using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>Covers the QS-77 incident: a persisted registry entry that fails the allowed-roots
/// check must be quarantined at boot instead of crashing the host.</summary>
public sealed class RepositoryRegistryQuarantineTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-tests", Guid.NewGuid().ToString("N"));
    private string InRootRepository => Path.Combine(testRoot, "in-root");
    private string OutsideRepository => Path.Combine(testRoot, "outside");
    private string HostRoot => Path.Combine(testRoot, "host");
    private TestApplication? application;

    [Fact]
    public async Task Boot_succeeds_with_a_poisoned_entry_which_is_quarantined_and_visible()
    {
        using var client = application!.CreateClient();

        using var health = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var repositories = body.GetProperty("repositories").EnumerateArray().ToArray();
        Assert.Equal(2, repositories.Length);

        var quarantined = Assert.Single(repositories, repository => repository.GetProperty("id").GetString() == "poisoned");
        var blockedReason = quarantined.GetProperty("blockedReason").GetString();
        Assert.NotNull(blockedReason);
        Assert.Contains(OutsideRepository, blockedReason, StringComparison.Ordinal);
        Assert.Contains(InRootRepository, blockedReason, StringComparison.Ordinal);

        var healthy = Assert.Single(repositories, repository => repository.GetProperty("id").GetString() == "default");
        Assert.Equal(JsonValueKind.Null, healthy.GetProperty("blockedReason").ValueKind);
    }

    [Fact]
    public async Task Quarantined_entry_is_excluded_from_use_while_the_in_root_entry_still_works()
    {
        using var client = application!.CreateClient();

        using var blockedFile = await client.GetAsync("/api/repos/poisoned/file?path=Foreign.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, blockedFile.StatusCode);

        using var healthyFile = await client.GetAsync("/api/repos/default/file?path=Sample.cs", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthyFile.StatusCode);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(InRootRepository);
        Directory.CreateDirectory(OutsideRepository);
        Directory.CreateDirectory(HostRoot);
        await File.WriteAllTextAsync(Path.Combine(InRootRepository, "Sample.cs"), "namespace Sample; public sealed class Subject;");
        await File.WriteAllTextAsync(Path.Combine(OutsideRepository, "Foreign.cs"), "namespace Foreign; public sealed class Secret;");
        await RunGitAsync(InRootRepository);
        await RunGitAsync(OutsideRepository);
        WriteRegistry();
        application = new TestApplication(InRootRepository, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
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

    private void WriteRegistry()
    {
        var path = Path.Combine(HostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", InRootRepository, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("poisoned", "Poisoned", OutsideRepository, null, 12000,
                new[] { "code", "security", "performance" }),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
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
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }
}
