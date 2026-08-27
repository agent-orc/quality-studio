using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class RepositoryRegistryTests : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-registry-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
    }

    [Fact]
    public async Task Boot_quarantines_an_out_of_root_persisted_entry_but_leaves_in_root_entries_usable()
    {
        var hostRoot = Path.Combine(testRoot, "scenario1", "host");
        var allowedRoot = Path.Combine(testRoot, "scenario1", "allowed-root");
        var inRootRepository = Path.Combine(allowedRoot, "in-root-repo");
        var outOfRootRepository = Path.Combine(testRoot, "scenario1", "outside", "poisoned-repo");
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(inRootRepository);
        Directory.CreateDirectory(outOfRootRepository);
        WriteRegistry(hostRoot, [
            new RepositoryRegistration("default", "Default", inRootRepository, null, 12000,
                ["code", "security", "performance"]),
            new RepositoryRegistration("poisoned", "Poisoned", outOfRootRepository, null, 12000,
                ["code", "security", "performance"]),
        ]);

        using var application = new TestApplication(allowedRoot, hostRoot);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/api/repos", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var repositories = body.GetProperty("repositories").EnumerateArray().ToArray();
        var inRootEntry = Assert.Single(repositories, entry => entry.GetProperty("id").GetString() == "default");
        var poisonedEntry = Assert.Single(repositories, entry => entry.GetProperty("id").GetString() == "poisoned");

        Assert.Equal(JsonValueKind.Null, inRootEntry.GetProperty("quarantineReason").ValueKind);
        Assert.Equal(inRootRepository, inRootEntry.GetProperty("rootPath").GetString());
        Assert.Equal("default", body.GetProperty("defaultRepositoryId").GetString());

        Assert.NotEqual(JsonValueKind.Null, poisonedEntry.GetProperty("quarantineReason").ValueKind);
        // The reason shown to API clients must stay path-free, matching PublicTitle everywhere else in this class
        // (rootPath itself is legitimately exposed for every repository, quarantined or not).
        var quarantineReason = poisonedEntry.GetProperty("quarantineReason").GetString();
        Assert.DoesNotContain(outOfRootRepository, quarantineReason, StringComparison.Ordinal);
        Assert.DoesNotContain(allowedRoot, quarantineReason, StringComparison.Ordinal);

        var registry = application.Services.GetRequiredService<RepositoryRegistry>();
        Assert.Equal(inRootRepository, registry.Get("default").RootPath);
        var exception = Assert.Throws<RepositoryRegistryValidationException>(() => registry.Get("poisoned"));
        Assert.Equal("Repository is quarantined", exception.PublicTitle);
    }

    [Fact]
    public async Task EnsureAllowedDirectory_names_the_offending_path_and_the_allowed_roots_for_diagnosis()
    {
        var hostRoot = Path.Combine(testRoot, "scenario2", "host");
        var allowedRoot = Path.Combine(testRoot, "scenario2", "allowed-root");
        var defaultRepository = Path.Combine(allowedRoot, "default-repo");
        var outsidePath = Path.Combine(testRoot, "scenario2", "outside", "not-allowed");
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(defaultRepository);
        Directory.CreateDirectory(outsidePath);
        WriteRegistry(hostRoot, [
            new RepositoryRegistration("default", "Default", defaultRepository, null, 12000,
                ["code", "security", "performance"]),
        ]);

        using var application = new TestApplication(allowedRoot, hostRoot);
        var registry = application.Services.GetRequiredService<RepositoryRegistry>();

        var request = new RepositoryRegistrationRequest(Id: "outside", DisplayName: "Outside", RootPath: outsidePath,
            GlobalInputsDirectory: null, InputBudgetCharacters: null, EnabledReviewKinds: ["code"]);
        var exception = await Assert.ThrowsAsync<RepositoryRegistryValidationException>(
            () => registry.CreateAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains(outsidePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(allowedRoot, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archiving_the_last_usable_repository_is_refused_even_when_a_quarantined_entry_remains()
    {
        var hostRoot = Path.Combine(testRoot, "scenario3", "host");
        var allowedRoot = Path.Combine(testRoot, "scenario3", "allowed-root");
        var usableRepository = Path.Combine(allowedRoot, "usable-repo");
        var outOfRootRepository = Path.Combine(testRoot, "scenario3", "outside", "poisoned-repo");
        Directory.CreateDirectory(hostRoot);
        Directory.CreateDirectory(usableRepository);
        Directory.CreateDirectory(outOfRootRepository);
        WriteRegistry(hostRoot, [
            new RepositoryRegistration("extra", "Extra", usableRepository, null, 12000,
                ["code", "security", "performance"]),
            new RepositoryRegistration("quarantined", "Quarantined", outOfRootRepository, null, 12000,
                ["code", "security", "performance"]),
        ]);

        using var application = new TestApplication(allowedRoot, hostRoot);
        var registry = application.Services.GetRequiredService<RepositoryRegistry>();

        // A quarantined entry never counted as "usable", so removing it never reduces the usable count.
        var archivedQuarantined = await registry.ArchiveAsync("quarantined", TestContext.Current.CancellationToken);
        Assert.True(archivedQuarantined.Archived);

        // "extra" is now the only entry left; archiving it would leave zero usable repositories.
        var exception = await Assert.ThrowsAsync<RepositoryRegistryValidationException>(
            () => registry.ArchiveAsync("extra", TestContext.Current.CancellationToken));
        Assert.Equal("The last usable repository cannot be archived.", exception.Message);
    }

    private static void WriteRegistry(string hostRoot, RepositoryRegistration[] entries)
    {
        var path = Path.Combine(hostRoot, ".quality-studio", "repositories.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private sealed class TestApplication(string allowedRoot, string hostRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(hostRoot);
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
