using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSecurityTests
{
    [Fact]
    public async Task Repository_scoped_identity_manages_its_project_rules_but_not_the_global_pool()
    {
        using var alice = CreateClient("alice", AliceToken);
        try
        {
            using var global = await alice.PutAsJsonAsync("/api/repos/default/rules/overrides/QS-CS-004?scope=global",
                new { enabled = false, reason = "Host-wide." }, TestContext.Current.CancellationToken);
            using var globalImport = await alice.PostAsJsonAsync("/api/repos/default/rules/import?scope=global",
                new
                {
                    ruleSet = new
                    {
                        schemaVersion = 1,
                        overrides = Array.Empty<object>(),
                        customRules = Array.Empty<object>(),
                        packs = Array.Empty<object>()
                    },
                    mode = "replace",
                    reason = "Wipe it."
                }, TestContext.Current.CancellationToken);
            using var foreign = await alice.PutAsJsonAsync("/api/repos/foreign/rules/overrides/QS-CS-004",
                new { enabled = false, reason = "Not mine." }, TestContext.Current.CancellationToken);
            using var project = await alice.PutAsJsonAsync("/api/repos/default/rules/overrides/QS-CS-004",
                new { enabled = false, reason = "No tests in this repository." }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, globalImport.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            Assert.Equal(HttpStatusCode.OK, project.StatusCode);
            Assert.False(File.Exists(Path.Combine(ForeignRepositoryRoot, ".quality", "rules", "overrides.json")));
        }
        finally
        {
            TemporaryDirectory.Delete(Path.Combine(RepositoryRoot, ".quality", "rules"));
        }
    }

    [Fact]
    public async Task Repository_scoped_identity_can_neither_read_nor_change_a_foreign_rule_pool()
    {
        using var alice = CreateClient("alice", AliceToken);
        var ruleSet = new
        {
            ruleSet = new { schemaVersion = 1, overrides = Array.Empty<object>(), customRules = Array.Empty<object>(), packs = Array.Empty<object>() },
            mode = "replace",
            reason = "Not mine.",
        };
        var pack = new { pack = new { id = "mine", title = "Mine", rules = new[] { "QS-CS-004" } }, reason = "Not mine." };
        var requests = new Func<Task<HttpResponseMessage>>[]
        {
            () => alice.GetAsync("/api/repos/foreign/rules", TestContext.Current.CancellationToken),
            () => alice.GetAsync("/api/repos/foreign/rules/export", TestContext.Current.CancellationToken),
            () => alice.GetAsync("/api/repos/foreign/rules/audit", TestContext.Current.CancellationToken),
            () => alice.PostAsJsonAsync("/api/repos/foreign/rules/import", ruleSet, TestContext.Current.CancellationToken),
            () => alice.PostAsJsonAsync("/api/repos/foreign/rules/import", new { ruleSet.ruleSet, dryRun = true }, TestContext.Current.CancellationToken),
            () => alice.PutAsJsonAsync("/api/repos/foreign/rules/overrides/QS-CS-004", new { enabled = false, reason = "Not mine." }, TestContext.Current.CancellationToken),
            () => alice.DeleteAsync("/api/repos/foreign/rules/overrides/QS-CS-004", TestContext.Current.CancellationToken),
            () => alice.PostAsJsonAsync("/api/repos/foreign/rules/custom/validate", new { content = "---\nid: TEAM-001\n---\n" }, TestContext.Current.CancellationToken),
            () => alice.PutAsJsonAsync("/api/repos/foreign/rules/custom/TEAM-001", new { content = "---\nid: TEAM-001\n---\n", reason = "Not mine." }, TestContext.Current.CancellationToken),
            () => alice.DeleteAsync("/api/repos/foreign/rules/custom/TEAM-001", TestContext.Current.CancellationToken),
            () => alice.PutAsJsonAsync("/api/repos/foreign/rules/packs/mine", pack, TestContext.Current.CancellationToken),
            () => alice.DeleteAsync("/api/repos/foreign/rules/packs/mine", TestContext.Current.CancellationToken),
            () => alice.PutAsJsonAsync("/api/repos/foreign/rules/applicability", new { packs = new[] { "house-style" }, reason = "Not mine." }, TestContext.Current.CancellationToken),
            () => alice.DeleteAsync("/api/repos/foreign/rules/applicability", TestContext.Current.CancellationToken),
        };

        foreach (var request in requests)
        {
            using var response = await request();
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(ForeignRepositoryRoot, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Combine(ForeignRepositoryRoot, ".quality", "rules")));
    }

    [Fact]
    public void Rule_pool_resolution_refuses_a_foreign_repository_on_its_own()
    {
        // The endpoints must not rely on the API middleware alone: resolving the rule pool for a
        // repository outside the caller's identity fails as "not found" even when called directly.
        var registry = application!.Services.GetRequiredService<RepositoryRegistry>();
        var security = application.Services.GetRequiredService<ApiSecurity>();
        var context = new DefaultHttpContext();
        security.SetIdentity(context, new ApiClientIdentity("alice", new HashSet<string> { "default" }, false));

        context.Request.RouteValues["repoId"] = "foreign";
        Assert.Throws<KeyNotFoundException>(() => RulePoolEndpoints.Resolve(context, registry, security));

        context.Request.RouteValues["repoId"] = "default";
        Assert.Equal("default", RulePoolEndpoints.Resolve(context, registry, security).Registration.Id);

        context.Request.RouteValues.Remove("repoId");
        security.SetIdentity(context, new ApiClientIdentity("bob", new HashSet<string> { "foreign" }, false));
        Assert.Throws<KeyNotFoundException>(() => RulePoolEndpoints.Resolve(context, registry, security));
    }
}
