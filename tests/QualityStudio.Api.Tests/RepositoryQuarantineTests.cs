using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Regression coverage for the 2026-08-11 incident: a persisted repository entry outside the
/// configured AllowedRoots must never crash the host during boot. It is quarantined instead.
/// </summary>
public sealed class RepositoryQuarantineTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-quarantine-tests", Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "default");
    private string OutsideRoot => Path.Combine(testRoot, "outside", "agent-studio-checkout");
    private string HostRoot => Path.Combine(testRoot, "host");
    private QuarantineApplication? application;

    [Fact]
    public async Task Boot_succeeds_and_quarantines_the_out_of_root_persisted_entry()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var repositories = body.GetProperty("repositories").EnumerateArray().ToDictionary(
            entry => entry.GetProperty("id").GetString()!, entry => entry);

        Assert.False(repositories["default"].GetProperty("blocked").GetBoolean());

        var external = repositories["external"];
        Assert.True(external.GetProperty("blocked").GetBoolean());
        var reason = external.GetProperty("blockReason").GetString();
        Assert.NotNull(reason);
        Assert.Contains(OutsideRoot, reason, StringComparison.Ordinal);
        Assert.Contains(RepositoryRoot, reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_root_entry_remains_usable_after_a_sibling_entry_is_quarantined()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/repos/default/tree?path=", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Quarantined_entry_is_excluded_from_use()
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/repos/external/tree?path=", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public async ValueTask InitializeAsync()
    {
        foreach (var directory in new[] { RepositoryRoot, OutsideRoot, HostRoot })
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await RunGitAsync(RepositoryRoot);
        WriteRegistry(HostRoot);
        application = new QuarantineApplication(RepositoryRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { Directory.Delete(testRoot, true); }
        catch (IOException) { }
    }

    private void WriteRegistry(string hostRoot)
    {
        var path = Path.Combine(hostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var entries = new[]
        {
            new RepositoryRegistration("default", "Default", RepositoryRoot, null, 12000,
                new[] { "code", "security", "performance" }),
            new RepositoryRegistration("external", "Agent Studio checkout", OutsideRoot, null, 12000,
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

    private sealed class QuarantineApplication(string root, string contentRoot) : WebApplicationFactory<Program>
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
