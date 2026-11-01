using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSecurityTests
{
    [Fact]
    public async Task External_reference_lookup_finds_only_repositories_the_client_may_reach()
    {
        using var admin = CreateClient("admin", AdminToken);
        using var tagged = await admin.PutAsJsonAsync("/api/repos/foreign", new
        {
            displayName = "Foreign",
            rootPath = ForeignRepositoryRoot,
            enabledReviewKinds = new[] { "code" },
            externalRefs = new[] { new { system = "agent-studio", id = "PROJ-FOREIGN" } },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tagged.StatusCode);

        // Bob reaches only "foreign", not the default repository, and still finds it.
        using var bob = CreateClient("bob", BobToken);
        var found = await bob.GetFromJsonAsync<JsonElement>("/api/v1/repos?externalRef=agent-studio:PROJ-FOREIGN",
            TestContext.Current.CancellationToken);
        Assert.Equal("foreign", Assert.Single(found.GetProperty("repositories").EnumerateArray())
            .GetProperty("id").GetString());

        // Alice cannot reach it, so for her it does not exist.
        using var alice = CreateClient("alice", AliceToken);
        using var hidden = await alice.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-FOREIGN",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.DoesNotContain(ForeignRepositoryRoot,
            await hidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var anonymous = CreateClient();
        using var unauthenticated = await anonymous.GetAsync("/api/v1/repos?externalRef=agent-studio:PROJ-FOREIGN",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task Capabilities_need_authentication_but_no_default_repository_access()
    {
        using var anonymous = CreateClient();
        using var unauthenticated = await anonymous.GetAsync("/api/v1/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var bob = CreateClient("bob", BobToken);
        using var capabilities = await bob.GetAsync("/api/v1/capabilities", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
    }

    [Fact]
    public async Task Only_a_registrar_may_attach_an_external_reference()
    {
        using var bob = CreateClient("bob", BobToken);

        using var response = await bob.PutAsJsonAsync("/api/repos/foreign", new
        {
            displayName = "Foreign",
            rootPath = ForeignRepositoryRoot,
            enabledReviewKinds = new[] { "code" },
            externalRefs = new[] { new { system = "agent-studio", id = "PROJ-FOREIGN" } },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
