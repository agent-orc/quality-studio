using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class AngularBudgetSensorDefaultTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-ng-budget-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(
        Path.GetTempPath(), "quality-studio-ng-budget-hosts", Guid.NewGuid().ToString("N"));
    private TestApplication? application;

    [Fact]
    public async Task Ng_budget_sensor_is_disabled_by_default_without_an_angular_project()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/sensors", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var sensor = Assert.Single(json.GetProperty("sensors").EnumerateArray());
        Assert.Equal("ng-budget", sensor.GetProperty("id").GetString());
        Assert.False(sensor.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Ng_budget_sensor_enables_with_a_repository_owned_command_when_angular_json_exists()
    {
        var frontendRoot = repositoryRoot + "-frontend";
        Directory.CreateDirectory(Path.Combine(frontendRoot, "frontend"));
        await File.WriteAllTextAsync(
            Path.Combine(frontendRoot, "frontend", "angular.json"), "{}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(frontendRoot, "frontend", "package.json"), "{}",
            TestContext.Current.CancellationToken);
        await RunGitInDirectoryAsync(frontendRoot, "init", "--quiet");

        try
        {
            using var client = application!.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/repos", new
            {
                id = "frontend-repo",
                displayName = "Frontend repo",
                rootPath = frontendRoot,
                inputBudgetCharacters = 8000,
                enabledReviewKinds = new[] { "code" },
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var response = await client.GetAsync(
                "/api/repos/frontend-repo/sensors", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var sensor = Assert.Single(json.GetProperty("sensors").EnumerateArray());
            Assert.Equal("ng-budget", sensor.GetProperty("id").GetString());
            Assert.True(sensor.GetProperty("enabled").GetBoolean());
            Assert.True(sensor.GetProperty("available").GetBoolean());
            var configuration = sensor.GetProperty("configuration");
            Assert.Equal(
                "node node_modules/@angular/cli/bin/ng.js build --configuration production",
                configuration.GetProperty("command").GetString());
            Assert.Equal(".quality/preflight/ng-budget.log", configuration.GetProperty("reportPath").GetString());
            Assert.Equal("frontend", configuration.GetProperty("workingDirectory").GetString());
        }
        finally
        {
            Directory.Delete(frontendRoot, true);
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(
            Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await RunGitInDirectoryAsync(repositoryRoot, "init", "--quiet");
        application = new TestApplication(repositoryRoot, hostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        try
        {
            Directory.Delete(repositoryRoot, true);
            Directory.Delete(hostRoot, true);
        }
        catch (IOException)
        {
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
                    ["QualityStudio:AllowedRoots:0"] = Path.GetDirectoryName(root),
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                    ["AgentStudio:ClientId"] = "quality-studio-test",
                    ["AgentStudio:Project"] = "QS",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
                services.RemoveAll<IReviewSensor>();
                services.AddSingleton<IReviewSensor>(
                    serviceProvider => serviceProvider.GetRequiredService<AngularBudgetSensor>());
            });
        }
    }
}
