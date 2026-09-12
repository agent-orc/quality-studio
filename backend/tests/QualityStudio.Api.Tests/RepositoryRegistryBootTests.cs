using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// F-16: one broken persisted registration must not keep the host from starting. It is loaded as
/// unavailable with its reason, every other registration keeps working, and repairing it by PUT brings
/// it back without a restart.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class RepositoryRegistryBootTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-boot-tests",
        Guid.NewGuid().ToString("N"));
    private string AllowedRoot => Path.Combine(testRoot, "allowed");
    private string HealthyRoot => Path.Combine(AllowedRoot, "healthy");
    private string SecondRoot => Path.Combine(AllowedRoot, "second");
    private string VanishedRoot => Path.Combine(AllowedRoot, "vanished");
    private string OutsideRoot => Path.Combine(testRoot, "outside");
    private string HostRoot => Path.Combine(testRoot, "host");
    private string RegistryPath => Path.Combine(HostRoot, ".quality-studio", "repositories.json");
    private BootApplication? application;

    [Fact]
    public async Task A_vanished_or_escaped_registration_is_reported_while_the_rest_keeps_working()
    {
        using var client = application!.CreateClient();

        var repositories = await client.GetFromJsonAsync<JsonElement>("/api/repos",
            TestContext.Current.CancellationToken);

        var served = repositories.GetProperty("repositories").EnumerateArray()
            .Select(repository => repository.GetProperty("id").GetString()).ToArray();
        Assert.Equal(["healthy", "second"], served.Order(StringComparer.Ordinal));

        var unavailable = repositories.GetProperty("unavailable").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry);
        Assert.Equal(["escaped", "vanished"], unavailable.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("unavailable", unavailable["vanished"].GetProperty("status").GetString());
        Assert.Equal(VanishedRoot, unavailable["vanished"].GetProperty("rootPath").GetString());
        Assert.Contains("does not exist", unavailable["vanished"].GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal("quarantined", unavailable["escaped"].GetProperty("status").GetString());
        Assert.Contains("outside the allowed roots", unavailable["escaped"].GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quarantined_registration_answers_as_unavailable_rather_than_as_a_broken_host()
    {
        using var client = application!.CreateClient();

        using var healthy = await client.GetAsync("/api/repos/healthy/tree?path=",
            TestContext.Current.CancellationToken);
        using var vanished = await client.GetAsync("/api/repos/vanished/tree?path=",
            TestContext.Current.CancellationToken);
        using var escaped = await client.GetAsync("/api/repos/escaped/file?path=Outside.cs",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, vanished.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, escaped.StatusCode);
    }

    [Fact]
    public async Task Repointing_a_quarantined_registration_brings_it_back_without_a_restart()
    {
        using var client = application!.CreateClient();

        using var repaired = await client.PutAsJsonAsync("/api/repos/vanished", new
        {
            displayName = "Vanished",
            rootPath = SecondRoot,
            enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, repaired.StatusCode);
        var repositories = await client.GetFromJsonAsync<JsonElement>("/api/repos",
            TestContext.Current.CancellationToken);
        Assert.Contains(repositories.GetProperty("repositories").EnumerateArray(),
            repository => repository.GetProperty("id").GetString() == "vanished");
        Assert.DoesNotContain(repositories.GetProperty("unavailable").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "vanished");
    }

    [Fact]
    public async Task Architecture_sensor_is_automatically_enabled_only_for_repositories_with_a_contract()
    {
        // The persisted registrations predate the new sensor and contain no sensor settings.
        await File.WriteAllTextAsync(Path.Combine(HealthyRoot, ArchitectureSensor.ContractFileName),
            """
            { "schemaVersion": 1, "requiredDirectories": [], "forbiddenSourcePaths": ["src"], "directoryRules": [] }
            """, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(HealthyRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(HealthyRoot, "src", "Misplaced.cs"), "class Misplaced;",
            TestContext.Current.CancellationToken);

        var registry = application!.Services.GetRequiredService<RepositoryRegistry>();
        var configured = Assert.Single(registry.Get("healthy").Sensors!, sensor => sensor.Id == "architecture");
        Assert.True(configured.Enabled);
        Assert.False(Assert.Single(registry.Get("second").Sensors!, sensor => sensor.Id == "architecture").Enabled);

        var sensors = application.Services.GetRequiredService<SensorRegistry>();
        var evidence = await new DeterministicEvidenceCollector(sensors).CollectAsync(HealthyRoot,
            [new ReviewSensorConfiguration(configured.Id, configured.Configuration)],
            TestContext.Current.CancellationToken);
        Assert.Equal("architecture/forbidden-source-path", Assert.Single(Assert.Single(evidence).Findings).RuleId);
    }

    [Fact]
    public async Task Listing_stays_consistent_while_registrations_are_mutated()
    {
        using var client = application!.CreateClient();
        // A hang guard, not a performance budget: twelve registrations each persist the
        // registry and queue a prewarm, and on a loaded host that crossed the old ten
        // seconds and cancelled a correct run.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var mutations = Task.Run(async () =>
        {
            for (var round = 0; round < 12; round++)
            {
                using var created = await client.PostAsJsonAsync("/api/repos", new
                {
                    id = $"churn-{round}",
                    displayName = $"Churn {round}",
                    rootPath = SecondRoot,
                    enabledReviewKinds = new[] { "code" },
                }, cancellation.Token);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }
        }, cancellation.Token);

        var reads = Task.Run(async () =>
        {
            while (!mutations.IsCompleted)
            {
                var repositories = await client.GetFromJsonAsync<JsonElement>("/api/repos", cancellation.Token);
                foreach (var repository in repositories.GetProperty("repositories").EnumerateArray())
                    Assert.False(string.IsNullOrEmpty(repository.GetProperty("id").GetString()));
            }
        }, cancellation.Token);

        await Task.WhenAll(mutations, reads);
    }

    public async ValueTask InitializeAsync()
    {
        foreach (var root in new[] { HealthyRoot, SecondRoot, OutsideRoot })
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Sample { }");
            await GitTestRepository.InitializeAsync(root);
        }
        await File.WriteAllTextAsync(Path.Combine(OutsideRoot, "Outside.cs"), "public class Outside { }");

        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var entries = new[]
        {
            new RepositoryRegistration("healthy", "Healthy", HealthyRoot, null, 12000, ["code"]),
            new RepositoryRegistration("second", "Second", SecondRoot, null, 12000, ["code"]),
            // Never created: the persisted working copy is simply gone.
            new RepositoryRegistration("vanished", "Vanished", VanishedRoot, null, 12000, ["code"]),
            // Points outside the configured allowed roots, which no request may reach.
            new RepositoryRegistration("escaped", "Escaped", OutsideRoot, null, 12000, ["code"]),
        };
        await File.WriteAllTextAsync(RegistryPath, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        application = new BootApplication(AllowedRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { TemporaryDirectory.Delete(testRoot); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class BootApplication(string allowedRoot, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = allowedRoot,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["QualityStudio:Security:Mode"] = "Local",
                }));
        }
    }
}
