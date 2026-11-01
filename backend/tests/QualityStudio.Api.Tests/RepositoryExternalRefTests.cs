using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// QSI-3 (AGT-3352, decision D2 option M2): Agent Studio registers a repository under a stable external
/// reference and finds it again by that reference instead of by path. The reference is unique per
/// <c>system:id</c>, a registration ahead of its clone is refused with a typed reason the caller can
/// retry on, and registry files written before the field existed load and persist unchanged.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class RepositoryExternalRefTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-external-ref-tests",
        Guid.NewGuid().ToString("N"));
    private string AllowedRoot => Path.Combine(testRoot, "allowed");
    private string LegacyRoot => Path.Combine(AllowedRoot, "legacy");
    private string StudioRoot => Path.Combine(AllowedRoot, "studio");
    private string OtherRoot => Path.Combine(AllowedRoot, "other");
    private string LateRoot => Path.Combine(AllowedRoot, "late");
    private string VanishedRoot => Path.Combine(AllowedRoot, "vanished");
    private string HostRoot => Path.Combine(testRoot, "host");
    private string RegistryPath => Path.Combine(HostRoot, ".quality-studio", "repositories.json");
    private Application? application;

    [Fact]
    public async Task A_registration_is_found_again_by_its_external_reference()
    {
        using var client = application!.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002", "https://studio.test/#/projects/PROJ-002")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var registered = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var reference = Assert.Single(registered.GetProperty("externalRefs").EnumerateArray());
        Assert.Equal("agent-studio", reference.GetProperty("system").GetString());
        Assert.Equal("PROJ-002", reference.GetProperty("id").GetString());
        Assert.Equal("https://studio.test/#/projects/PROJ-002", reference.GetProperty("url").GetString());

        foreach (var spelling in new[] { "agent-studio:PROJ-002", "agent-studio:proj-002", "Agent-Studio:PROJ-002" })
        {
            var found = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v1/repos?externalRef={Uri.EscapeDataString(spelling)}", TestContext.Current.CancellationToken);
            var repository = Assert.Single(found.GetProperty("repositories").EnumerateArray());
            Assert.Equal("proj-002", repository.GetProperty("id").GetString());
            Assert.Equal(StudioRoot, repository.GetProperty("rootPath").GetString());
            Assert.Empty(found.GetProperty("unavailable").EnumerateArray());
        }

        using var absent = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-999",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var otherSystem = await client.GetAsync("/api/v1/repos?externalRef=github:PROJ-002",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, otherSystem.StatusCode);

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(RegistryPath, TestContext.Current.CancellationToken))!
            .AsArray().Single(entry => (string?)entry!["id"] == "proj-002")!;
        Assert.Equal("agent-studio:PROJ-002",
            $"{persisted["externalRefs"]![0]!["system"]}:{persisted["externalRefs"]![0]!["id"]}");
    }

    [Fact]
    public async Task A_second_registration_with_the_same_external_reference_is_refused_with_409()
    {
        using var client = application!.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Different id, different path, same reference spelled in another case.
        using var duplicate = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-003", OtherRoot, Reference("proj-002")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("external-ref-conflict", problem.GetProperty("reason").GetString());
        Assert.Equal("proj-002", problem.GetProperty("repositoryId").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));

        // Claiming it by editing another registration is refused the same way, and nothing is written.
        using var claimed = await client.PutAsJsonAsync("/api/repos/legacy",
            Registration("legacy", LegacyRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, claimed.StatusCode);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(listed.GetProperty("repositories").EnumerateArray(),
            repository => repository.GetProperty("id").GetString() == "proj-003");
        var found = await client.GetFromJsonAsync<JsonElement>("/api/v1/repos?externalRef=agent-studio:PROJ-002",
            TestContext.Current.CancellationToken);
        Assert.Equal("proj-002", Assert.Single(found.GetProperty("repositories").EnumerateArray())
            .GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_registration_ahead_of_its_checkout_is_refused_as_root_missing_and_a_retry_succeeds()
    {
        using var client = application!.CreateClient();
        var request = Registration("proj-late", LateRoot, Reference("PROJ-LATE"));

        using var early = await client.PostAsJsonAsync("/api/repos", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        var problem = await early.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("root-missing", problem.GetProperty("reason").GetString());
        Assert.Equal("Repository path does not exist", problem.GetProperty("title").GetString());
        Assert.DoesNotContain(LateRoot, problem.GetRawText(), StringComparison.Ordinal);
        using var notYet = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-LATE",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notYet.StatusCode);

        // The clone lands; the reconcile loop retries the identical request.
        await CreateCheckoutAsync(LateRoot);
        using var retried = await client.PostAsJsonAsync("/api/repos", request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var found = await client.GetFromJsonAsync<JsonElement>("/api/v1/repos?externalRef=agent-studio:PROJ-LATE",
            TestContext.Current.CancellationToken);
        Assert.Equal("proj-late", Assert.Single(found.GetProperty("repositories").EnumerateArray())
            .GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_directory_without_git_is_refused_with_its_own_typed_reason()
    {
        Directory.CreateDirectory(LateRoot);
        using var client = application!.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-late", LateRoot, Reference("PROJ-LATE")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("root-not-git", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Registrations_without_external_references_load_and_persist_unchanged()
    {
        using var client = application!.CreateClient();

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
        var legacy = Assert.Single(listed.GetProperty("repositories").EnumerateArray(),
            repository => repository.GetProperty("id").GetString() == "legacy");
        Assert.False(legacy.TryGetProperty("externalRefs", out _));
        Assert.Equal(LegacyRoot, legacy.GetProperty("rootPath").GetString());

        // Any write rewrites the whole file; the old entry must come back without the new field.
        using var created = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(RegistryPath, TestContext.Current.CancellationToken))!
            .AsArray();
        var legacyEntry = persisted.Single(entry => (string?)entry!["id"] == "legacy")!.AsObject();
        Assert.False(legacyEntry.ContainsKey("externalRefs"));
        Assert.Equal(LegacyRoot, (string?)legacyEntry["rootPath"]);
    }

    [Fact]
    public async Task An_edit_that_omits_external_references_keeps_them_and_an_empty_list_clears_them()
    {
        using var client = application!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The repository dialog predates the field and never sends it.
        using var renamed = await client.PutAsJsonAsync("/api/repos/proj-002", new
        {
            displayName = "Renamed",
            rootPath = StudioRoot,
            enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var found = await client.GetFromJsonAsync<JsonElement>("/api/v1/repos?externalRef=agent-studio:PROJ-002",
            TestContext.Current.CancellationToken);
        Assert.Equal("Renamed", Assert.Single(found.GetProperty("repositories").EnumerateArray())
            .GetProperty("displayName").GetString());

        using var cleared = await client.PutAsJsonAsync("/api/repos/proj-002", new
        {
            displayName = "Renamed",
            rootPath = StudioRoot,
            enabledReviewKinds = new[] { "code" },
            externalRefs = Array.Empty<object>(),
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var gone = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-002",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Released, so another registration may now take it.
        using var taken = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-003", OtherRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, taken.StatusCode);
    }

    [Theory]
    [InlineData("", "PROJ-002", null)]
    [InlineData("Agent Studio", "PROJ-002", null)]
    [InlineData("agent:studio", "PROJ-002", null)]
    [InlineData("agent-studio", "", null)]
    [InlineData("agent-studio", "PROJ 002", null)]
    [InlineData("agent-studio", "PROJ-002", "javascript:alert(1)")]
    [InlineData("agent-studio", "PROJ-002", "/projects/PROJ-002")]
    public async Task A_malformed_external_reference_is_refused(string system, string id, string? url)
    {
        using var client = application!.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, new { system, id, url }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("invalid-external-ref", problem.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task One_registration_may_not_name_the_same_reference_twice()
    {
        using var client = application!.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002"), Reference("proj-002")),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("PROJ-002")]
    [InlineData("agent-studio:")]
    [InlineData(":PROJ-002")]
    public async Task A_malformed_lookup_is_a_bad_request(string externalRef)
    {
        using var client = application!.CreateClient();

        using var response = await client.GetAsync($"/api/v1/repos?externalRef={Uri.EscapeDataString(externalRef)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_filter_the_versioned_collection_lists_like_the_registry()
    {
        using var client = application!.CreateClient();

        var versioned = await client.GetFromJsonAsync<JsonElement>("/api/v1/repos", TestContext.Current.CancellationToken);
        var legacy = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);

        Assert.Equal(legacy.GetRawText(), versioned.GetRawText());
    }

    [Fact]
    public async Task A_reference_whose_checkout_vanished_is_found_and_reported_as_unavailable()
    {
        using var client = application!.CreateClient();

        var found = await client.GetFromJsonAsync<JsonElement>("/api/v1/repos?externalRef=agent-studio:PROJ-GONE",
            TestContext.Current.CancellationToken);

        Assert.Empty(found.GetProperty("repositories").EnumerateArray());
        var unavailable = Assert.Single(found.GetProperty("unavailable").EnumerateArray());
        Assert.Equal("vanished", unavailable.GetProperty("id").GetString());
        Assert.Equal("unavailable", unavailable.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_archived_registration_keeps_its_reference_and_is_found_only_when_asked_for()
    {
        using var client = application!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var archived = await client.DeleteAsync("/api/repos/proj-002", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        using var active = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-002",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, active.StatusCode);
        var withArchived = await client.GetFromJsonAsync<JsonElement>(
            "/api/v1/repos?externalRef=agent-studio:PROJ-002&includeArchived=true",
            TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(withArchived.GetProperty("repositories").EnumerateArray())
            .GetProperty("archived").GetBoolean());

        using var duplicate = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-003", OtherRoot, Reference("PROJ-002")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task A_hand_edited_registry_holding_one_reference_twice_answers_ambiguous_rather_than_guessing()
    {
        await application!.DisposeAsync();
        var entries = JsonNode.Parse(await File.ReadAllTextAsync(RegistryPath, TestContext.Current.CancellationToken))!
            .AsArray();
        foreach (var id in new[] { "legacy", "other" })
        {
            var entry = entries.Single(candidate => (string?)candidate!["id"] == id)!.AsObject();
            entry["externalRefs"] = new JsonArray(new JsonObject { ["system"] = "agent-studio", ["id"] = "PROJ-DUP" });
        }
        await File.WriteAllTextAsync(RegistryPath, entries.ToJsonString(), TestContext.Current.CancellationToken);
        application = new Application(AllowedRoot, HostRoot);
        using var client = application.CreateClient();

        using var response = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-DUP",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("external-ref-ambiguous", problem.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The schema in schemas/ is validated against sample documents in the analysis tests; this holds it
    /// to what the host really writes, so a new registration field cannot ship undeclared.
    /// </summary>
    [Fact]
    public async Task Every_property_the_registry_writes_is_declared_in_its_schema()
    {
        using var client = application!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/repos",
            Registration("proj-002", StudioRoot, Reference("PROJ-002", "https://studio.test/")),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var schema = JsonNode.Parse(await File.ReadAllTextAsync(SchemaPath(), TestContext.Current.CancellationToken))!;
        var declared = schema["$defs"]!["registration"]!["properties"]!.AsObject().Select(pair => pair.Key).ToHashSet();
        var declaredRef = schema["$defs"]!["externalRef"]!["properties"]!.AsObject().Select(pair => pair.Key).ToHashSet();

        var persisted = JsonNode.Parse(await File.ReadAllTextAsync(RegistryPath, TestContext.Current.CancellationToken))!
            .AsArray();

        foreach (var entry in persisted.Select(entry => entry!.AsObject()))
        {
            Assert.Equal([], entry.Select(pair => pair.Key).Except(declared));
            foreach (var reference in entry["externalRefs"]?.AsArray() ?? [])
                Assert.Equal([], reference!.AsObject().Select(pair => pair.Key).Except(declaredRef));
        }
    }

    [Fact]
    public async Task Capabilities_announce_the_external_reference_feature()
    {
        using var client = application!.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities",
            TestContext.Current.CancellationToken);

        Assert.Equal("quality-studio", capabilities.GetProperty("product").GetString());
        Assert.Contains(capabilities.GetProperty("contracts").EnumerateArray(),
            contract => contract.GetString() == "agent-studio-integration/1");
        Assert.Contains(capabilities.GetProperty("features").EnumerateArray(),
            feature => feature.GetString() == "external-ref");
    }

    public async ValueTask InitializeAsync()
    {
        foreach (var root in new[] { LegacyRoot, StudioRoot, OtherRoot }) await CreateCheckoutAsync(root);

        // Written as raw JSON in the shape registry files had before external references existed.
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var registry = new JsonArray(
            LegacyEntry("legacy", LegacyRoot),
            LegacyEntry("other", OtherRoot),
            LegacyEntry("vanished", VanishedRoot, new JsonArray(
                new JsonObject { ["system"] = "agent-studio", ["id"] = "PROJ-GONE" })));
        await File.WriteAllTextAsync(RegistryPath, registry.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        application = new Application(AllowedRoot, HostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        try { TemporaryDirectory.Delete(testRoot); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static JsonObject LegacyEntry(string id, string root, JsonArray? externalRefs = null)
    {
        var entry = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = id,
            ["rootPath"] = root,
            ["globalInputsDirectory"] = null,
            ["inputBudgetCharacters"] = 12000,
            ["enabledReviewKinds"] = new JsonArray("code"),
            ["sensors"] = null,
            ["archived"] = false,
            ["defaultReviewTokenCap"] = null,
            ["defaultReviewCostCap"] = null,
        };
        if (externalRefs is not null) entry["externalRefs"] = externalRefs;
        return entry;
    }

    private static async Task CreateCheckoutAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "public class Sample { }");
        await GitTestRepository.InitializeAsync(root);
    }

    private static string SchemaPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QualityStudio.slnx")))
                return Path.Combine(directory.FullName, "schemas", "repository-registry.v1.schema.json");
        }
        throw new InvalidOperationException("The repository root holding QualityStudio.slnx was not found.");
    }

    private static object Reference(string id, string? url = null) => new { system = "agent-studio", id, url };

    private static object Registration(string id, string root, params object[] externalRefs) => new
    {
        id,
        displayName = id,
        rootPath = root,
        enabledReviewKinds = new[] { "code" },
        externalRefs,
    };

    private sealed class Application(string allowedRoot, string contentRoot) : WebApplicationFactory<Program>
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
