using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class AgentStudioImportTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-import-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-import-hosts", Guid.NewGuid().ToString("N"));
    private readonly string secondProjectRoot = Path.Combine(Path.GetTempPath(), "quality-studio-import-second", Guid.NewGuid().ToString("N"));
    private readonly string missingProjectPath = Path.Combine(Path.GetTempPath(), "quality-studio-import-missing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Import_onboards_new_projects_skips_known_paths_and_reports_failures()
    {
        var handler = new StubHandler(new object[]
        {
            new { id = "PROJ-001", displayName = "Quality Studio", shortCode = "QS", repositoryPath = repositoryRoot, archived = false },
            new { id = "PROJ-002", displayName = "Second Project", shortCode = "SEC", repositoryPath = secondProjectRoot, archived = false },
            new { id = "PROJ-003", displayName = "Project without a local checkout", shortCode = "NP", repositoryPath = (string?)null, archived = false },
            new { id = "PROJ-004", displayName = "Archived project", shortCode = "ARC", repositoryPath = secondProjectRoot, archived = true },
            new { id = "PROJ-005", displayName = "Missing on disk", shortCode = "MISS", repositoryPath = missingProjectPath, archived = false },
        });
        using var application = new TestApplication(repositoryRoot, hostRoot, handler);
        using var client = application.CreateClient();

        using var response = await client.PostAsync("/api/repos/import-from-agent-studio", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, json.GetProperty("imported").GetInt32());
        Assert.Equal(1, json.GetProperty("skipped").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());

        var results = json.GetProperty("results").EnumerateArray().ToArray();
        // The archived project and the one without a repository path are excluded entirely, not reported as failures.
        Assert.Equal(3, results.Length);

        var quality = Assert.Single(results, result => result.GetProperty("projectId").GetString() == "PROJ-001");
        Assert.Equal("skipped", quality.GetProperty("status").GetString());

        var second = Assert.Single(results, result => result.GetProperty("projectId").GetString() == "PROJ-002");
        Assert.Equal("imported", second.GetProperty("status").GetString());
        Assert.Equal("sec", second.GetProperty("repositoryId").GetString());

        var missing = Assert.Single(results, result => result.GetProperty("projectId").GetString() == "PROJ-005");
        Assert.Equal("failed", missing.GetProperty("status").GetString());
        Assert.Contains("does not exist", missing.GetProperty("reason").GetString());

        using var reimport = await client.PostAsync("/api/repos/import-from-agent-studio", null, TestContext.Current.CancellationToken);
        var reimportJson = await reimport.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0, reimportJson.GetProperty("imported").GetInt32());
        Assert.Equal(2, reimportJson.GetProperty("skipped").GetInt32());

        using var repos = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);
        var reposJson = await repos.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(2, reposJson.GetProperty("repositories").GetArrayLength());
    }

    [Fact]
    public async Task Import_returns_a_clear_error_and_leaves_the_registry_untouched_when_agent_studio_is_offline()
    {
        using var application = new TestApplication(repositoryRoot, hostRoot, handler: new OfflineHandler());
        using var client = application.CreateClient();

        using var response = await client.PostAsync("/api/repos/import-from-agent-studio", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        using var repos = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);
        var reposJson = await repos.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Single(reposJson.GetProperty("repositories").EnumerateArray());
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(secondProjectRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await RunGitInDirectoryAsync(repositoryRoot, "init", "--quiet");
        await RunGitInDirectoryAsync(secondProjectRoot, "init", "--quiet");
    }

    public ValueTask DisposeAsync()
    {
        foreach (var directory in new[] { repositoryRoot, hostRoot, secondProjectRoot })
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }

        return ValueTask.CompletedTask;
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

    private sealed class StubHandler(IReadOnlyList<object> projects) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(new Uri("http://agent-studio.test/api/projects"), request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(projects, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));
    }

    private sealed class TestApplication(string root, string contentRoot, HttpMessageHandler handler) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                }));
            builder.ConfigureServices(services => services.AddSingleton(_ => new HttpClient(handler)));
        }
    }
}
