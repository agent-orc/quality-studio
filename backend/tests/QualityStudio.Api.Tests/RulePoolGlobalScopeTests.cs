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
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// The global rule folder lives in the process-wide data root, which every in-process host of this
/// test assembly resolves rules from. A global rule written while another suite reviews a file
/// would change that suite's effective input hash, so these tests run alone.
/// </summary>
[CollectionDefinition(nameof(RulePoolGlobalScopeCollection), DisableParallelization = true)]
public sealed class RulePoolGlobalScopeCollection;

[Collection(nameof(RulePoolGlobalScopeCollection))]
[Trait("Category", "ToolBound")]
public sealed class RulePoolGlobalScopeTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "quality-studio-rule-pool-global", Guid.NewGuid().ToString("N"));
    private string RepositoryRoot => Path.Combine(testRoot, "repository");
    private string SecondRepositoryRoot => Path.Combine(testRoot, "second");
    private static string GlobalRules => new RuleCatalogueResolver().GlobalRulesDirectory;
    private Application? application;

    [Fact]
    public async Task A_global_custom_rule_and_override_reach_every_repository_and_are_audited_globally()
    {
        using var client = application!.CreateClient();
        using var registered = await client.PostAsJsonAsync("/api/repos", new
        {
            id = "second",
            displayName = "Second",
            rootPath = SecondRepositoryRoot,
            inputBudgetCharacters = 8000,
            enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        using var rule = await client.PutAsJsonAsync("/api/rules/custom/HOST-GN-001?scope=global", new
        {
            content = HostRule(),
            reason = "Every repository on this host documents its public API.",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rule.StatusCode);
        using var severity = await client.PutAsJsonAsync("/api/rules/overrides/HOST-GN-001?scope=global",
            new { severity = "high", reason = "Undocumented APIs caused integration failures." }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, severity.StatusCode);

        var second = await client.GetFromJsonAsync<JsonElement>("/api/repos/second/rules", TestContext.Current.CancellationToken);
        var hostRule = Assert.Single(second.GetProperty("rules").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "HOST-GN-001");
        Assert.Equal("global", hostRule.GetProperty("origin").GetString());
        Assert.Equal("high", hostRule.GetProperty("severity").GetString());
        Assert.Contains("global", second.GetProperty("sources").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(GlobalRules, second.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var projectAttempt = await client.PutAsJsonAsync("/api/repos/second/rules/custom/HOST-GN-001",
            new { content = HostRule(), reason = "Shadow it." }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, projectAttempt.StatusCode);

        var audit = await client.GetFromJsonAsync<JsonElement>("/api/rules/audit?scope=global", TestContext.Current.CancellationToken);
        Assert.Equal(["override.set", "custom-rule.put"],
            audit.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()));
        var projectAudit = await client.GetFromJsonAsync<JsonElement>("/api/rules/audit", TestContext.Current.CancellationToken);
        Assert.Empty(projectAudit.GetProperty("entries").EnumerateArray());

        using var export = await client.GetAsync("/api/rules/export?scope=global", TestContext.Current.CancellationToken);
        var ruleSet = await export.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("global", ruleSet.GetProperty("scope").GetString());
        Assert.Single(ruleSet.GetProperty("overrides").EnumerateArray());
    }

    [Fact]
    public async Task A_global_rule_cannot_collide_with_a_registered_repository_rule()
    {
        var custom = Path.Combine(SecondRepositoryRoot, ".quality", "rules", "custom");
        Directory.CreateDirectory(custom);
        await File.WriteAllTextAsync(Path.Combine(custom, "HOST-GN-001.md"), HostRule(), TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();
        using var registered = await client.PostAsJsonAsync("/api/repos", new
        {
            id = "second", displayName = "Second", rootPath = SecondRepositoryRoot,
            inputBudgetCharacters = 8000, enabledReviewKinds = new[] { "code" },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        using var response = await client.PutAsJsonAsync("/api/rules/custom/HOST-GN-001?scope=global", new
        {
            content = HostRule(), reason = "Share this rule.",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(GlobalRules, "custom", "HOST-GN-001.md")));
        var second = await client.GetFromJsonAsync<JsonElement>("/api/repos/second/rules", TestContext.Current.CancellationToken);
        Assert.True(second.GetProperty("valid").GetBoolean());
    }

    private static string HostRule() => """
        ---
        id: HOST-GN-001
        version: 1.0.0
        title: Document every public API surface
        technology: generic
        kinds: [code]
        category: documentation
        severity: medium
        defaultOn: true
        autofixable: false
        deterministicRuleIds: []
        since: 1.0.0
        ---

        ## Statement

        Every public endpoint or exported type states its contract in a doc comment.

        ## Rationale

        Consumers integrate against the contract, not the implementation.

        ## Detection

        Look for public endpoints and exported types without a contract comment. Internal types do not count.

        ## Good example

        ```text
        /// Returns the resolved rule pool for a repository.
        ```

        ## Bad example

        ```text
        public IResult Rules() => ...
        ```

        ## Change history

        - 1.0.0 (2026-09-28): Initial rule.
        """;

    public async ValueTask InitializeAsync()
    {
        foreach (var root in new[] { RepositoryRoot, SecondRepositoryRoot })
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.cs"), "namespace Sample; public sealed class Subject;");
            await GitTestRepository.InitializeAsync(root);
        }
        Directory.CreateDirectory(Path.Combine(testRoot, "host"));
        TemporaryDirectory.Delete(GlobalRules);
        application = new Application(RepositoryRoot, testRoot, Path.Combine(testRoot, "host"));
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null) await application.DisposeAsync();
        TemporaryDirectory.Delete(GlobalRules);
        RuleCatalogueResolver.Invalidate();
        TemporaryDirectory.Delete(testRoot);
    }

    private sealed class Application(string root, string allowedRoot, string contentRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(contentRoot);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["QualityStudio:RepositoryRoot"] = root,
                    ["QualityStudio:AllowedRoots:0"] = allowedRoot,
                    ["AgentStudio:BaseUrl"] = "http://agent-studio.test",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<QuotaService>();
                services.AddSingleton(new QuotaService([]));
            });
        }
    }
}
