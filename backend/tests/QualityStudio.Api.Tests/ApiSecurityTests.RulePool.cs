using System.Net;
using System.Net.Http.Json;
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
                new { ruleSet = new { schemaVersion = 1, overrides = Array.Empty<object>(), customRules = Array.Empty<object>(),
                    packs = Array.Empty<object>() }, mode = "replace", reason = "Wipe it." }, TestContext.Current.CancellationToken);
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
}
