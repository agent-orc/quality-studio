using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Dossier AGT-3352, D2 option M2: a registration carries a stable external reference, unique per
/// system:id, and Agent Studio finds it again by that reference instead of by path.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed partial class ApiSmokeTests
{
    [Fact]
    public async Task External_reference_is_unique_and_finds_its_repository()
    {
        var firstRoot = repositoryRoot + "-external-first";
        var secondRoot = repositoryRoot + "-external-second";
        foreach (var root in new[] { firstRoot, secondRoot })
        {
            Directory.CreateDirectory(root);
            await GitTestRepository.InitializeAsync(root, TestContext.Current.CancellationToken);
        }

        try
        {
            using var client = application!.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/repos",
                Registration("proj-002", firstRoot, "PROJ-002"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdJson = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var reference = Assert.Single(createdJson.GetProperty("externalRefs").EnumerateArray());
            Assert.Equal("agent-studio", reference.GetProperty("system").GetString());
            Assert.Equal("PROJ-002", reference.GetProperty("id").GetString());

            using var duplicate = await client.PostAsJsonAsync("/api/repos",
                Registration("other", secondRoot, "proj-002"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

            using var found = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-002",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            var foundJson = await found.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("proj-002",
                Assert.Single(foundJson.GetProperty("repositories").EnumerateArray()).GetProperty("id").GetString());

            using var absent = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-999",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            using var malformed = await client.GetAsync("/api/v1/repos?externalRef=PROJ-002",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            // An edit that leaves externalRefs out keeps the stored reference.
            using var edited = await client.PutAsJsonAsync("/api/repos/proj-002", new
            {
                displayName = "Renamed",
                rootPath = firstRoot,
                enabledReviewKinds = new[] { "code" },
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.Contains("\"PROJ-002\"", await File.ReadAllTextAsync(
                Path.Combine(hostRoot, ".quality-studio", "repositories.json"), TestContext.Current.CancellationToken));

            using var normalizedEdit = await client.PutAsJsonAsync("/api/repos/proj-002", new
            {
                displayName = "Renamed",
                rootPath = firstRoot,
                enabledReviewKinds = new[] { "code" },
                externalRefs = new[] { new
                {
                    system = "  AGENT-STUDIO  ",
                    id = "  PROJ-002  ",
                    url = "  https://studio.example/projects/PROJ-002  ",
                } },
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, normalizedEdit.StatusCode);
            var updatedJson = await normalizedEdit.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var normalizedRef = Assert.Single(updatedJson.GetProperty("externalRefs").EnumerateArray());
            Assert.Equal("agent-studio", normalizedRef.GetProperty("system").GetString());
            Assert.Equal("PROJ-002", normalizedRef.GetProperty("id").GetString());
            Assert.Equal("https://studio.example/projects/PROJ-002", normalizedRef.GetProperty("url").GetString());
            var registryJson = await File.ReadAllTextAsync(
                Path.Combine(hostRoot, ".quality-studio", "repositories.json"), TestContext.Current.CancellationToken);
            Assert.DoesNotContain("  AGENT-STUDIO  ", registryJson);
            Assert.DoesNotContain("  PROJ-002  ", registryJson);
            using var registryDocument = JsonDocument.Parse(registryJson);
            var persistedRepository = Assert.Single(registryDocument.RootElement.EnumerateArray(),
                repository => repository.GetProperty("id").GetString() == "proj-002");
            var persistedRef = Assert.Single(persistedRepository.GetProperty("externalRefs").EnumerateArray());
            Assert.Equal("agent-studio", persistedRef.GetProperty("system").GetString());
            Assert.Equal("PROJ-002", persistedRef.GetProperty("id").GetString());

            // The seeded registration predates the field and still loads without it.
            var listed = await client.GetFromJsonAsync<JsonElement>("/api/repos", TestContext.Current.CancellationToken);
            var legacy = Assert.Single(listed.GetProperty("repositories").EnumerateArray(),
                repository => repository.GetProperty("id").GetString() == "default");
            Assert.Equal(JsonValueKind.Null, legacy.GetProperty("externalRefs").ValueKind);

            var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/v1/capabilities",
                TestContext.Current.CancellationToken);
            Assert.Contains(capabilities.GetProperty("features").EnumerateArray(),
                feature => feature.GetString() == "external-ref");
        }
        finally
        {
            TemporaryDirectory.Delete(firstRoot);
            TemporaryDirectory.Delete(secondRoot);
        }
    }

    [Fact]
    public async Task Registration_for_a_missing_root_is_refused_as_root_missing_and_succeeds_on_retry()
    {
        var pendingRoot = repositoryRoot + "-pending";
        try
        {
            using var client = application!.CreateClient();
            using var refused = await client.PostAsJsonAsync("/api/repos",
                Registration("proj-021", pendingRoot, "PROJ-021"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("root-missing", problem.GetProperty("reason").GetString());

            Directory.CreateDirectory(pendingRoot);
            await GitTestRepository.InitializeAsync(pendingRoot, TestContext.Current.CancellationToken);
            using var retried = await client.PostAsJsonAsync("/api/repos",
                Registration("proj-021", pendingRoot, "PROJ-021"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
            using var found = await client.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-021",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        }
        finally
        {
            TemporaryDirectory.Delete(pendingRoot);
        }
    }

    private static object Registration(string id, string root, string projectId) => new
    {
        id,
        displayName = id,
        rootPath = root,
        enabledReviewKinds = new[] { "code" },
        externalRefs = new[] { new { system = "agent-studio", id = projectId } },
    };
}
