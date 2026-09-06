using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

public sealed class ApiSmokeTests : IAsyncLifetime
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "quality-studio-api-tests", Guid.NewGuid().ToString("N"));
    private readonly string hostRoot = Path.Combine(Path.GetTempPath(), "quality-studio-api-hosts", Guid.NewGuid().ToString("N"));
    private TestApplication? application;

    [Fact]
    public async Task Tree_returns_derived_hierarchy_and_kind_states()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var project = Assert.Single(json.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.Equal("project", project.GetProperty("level").GetString());
        Assert.True(project.GetProperty("kinds").TryGetProperty("code", out var code));
        Assert.Equal("missing", code.GetProperty("overall").GetString());
        var module = Assert.Single(project.GetProperty("children").EnumerateArray());
        Assert.Equal("Sample", module.GetProperty("name").GetString());
        // Build output is not an MSBuild compile item, so it is never a scope candidate and never
        // reaches the exclusion list. A gitignored source outside build output still is one.
        var excluded = Assert.Single(module.GetProperty("excluded").EnumerateArray());
        Assert.Equal("generated/Generated.cs", excluded.GetProperty("path").GetString());
        Assert.Contains(".gitignore:2", excluded.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FlattenTree(json.RootElement.GetProperty("nodes")),
            node => node.GetProperty("path").GetString() == "bin/Generated.cs");
    }

    [Fact]
    public async Task Tree_returns_etag_and_honours_conditional_request()
    {
        using var client = application!.CreateClient();
        using var first = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);
        Assert.NotNull(first.Headers.ETag);
        var etag = first.Headers.ETag.Tag;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tree?path=");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var cached = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        Assert.Equal(etag, cached.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Project_returns_repository_dashboard_and_honours_conditional_request()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/project", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.True(response.Headers.TryGetValues("Server-Timing", out var serverTiming));
        var timing = Assert.Single(serverTiming);
        Assert.Contains("git-status;dur=", timing, StringComparison.Ordinal);
        Assert.Contains("scan;dur=", timing, StringComparison.Ordinal);
        Assert.Contains("review-meta-discovery;dur=", timing, StringComparison.Ordinal);
        Assert.Contains("projection;dur=", timing, StringComparison.Ordinal);
        var dashboard = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(3, dashboard.GetProperty("grades").GetArrayLength());
        Assert.Equal(3, dashboard.GetProperty("metrics").GetProperty("fileCount").GetInt32());
        Assert.True(dashboard.GetProperty("hotspots").GetArrayLength() <= 30);

        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/project");
        cachedRequest.Headers.TryAddWithoutValidation("If-None-Match", response.Headers.ETag.Tag);
        using var cached = await client.SendAsync(cachedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
    }

    [Fact]
    public async Task Mixed_repository_tree_exposes_typescript_and_can_queue_file_review()
    {
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "frontend", "src", "app"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "frontend", "angular.json"),
            "{\"projects\":{\"frontend\":{\"root\":\"\",\"sourceRoot\":\"src\"}}}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "frontend", "src", "app", "app.component.ts"),
            "@Component({standalone: true}) export class AppComponent {}", TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();

        using var treeResponse = await client.GetAsync("/api/tree?path=", TestContext.Current.CancellationToken);
        var tree = await treeResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var nodes = FlattenTree(tree.GetProperty("nodes")).ToArray();
        Assert.Contains(nodes, node => node.GetProperty("path").GetString() == "frontend/src/app/app.component.ts");

        using var review = await client.PostAsJsonAsync("/api/review", new
        {
            path = "frontend/src/app/app.component.ts",
            kind = "code",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
        var accepted = await review.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("frontend/src/app/app.component.ts", accepted.GetProperty("path").GetString());
        Assert.Equal(1, accepted.GetProperty("totalFiles").GetInt32());
    }

    // The whole-picture pass: a module node plans one operation per member plus the aggregate,
    // and the aggregate prompt is the module template over the member digest.
    [Fact]
    public async Task Module_node_plans_an_aggregate_operation_and_can_be_queued()
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.csproj",
            kind = "code",
            cliType = "codex",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        var preflight = await estimate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("module", preflight.GetProperty("level").GetString());
        Assert.Equal(1, preflight.GetProperty("estimate").GetProperty("files").GetInt32());
        Assert.Equal(2, preflight.GetProperty("estimate").GetProperty("operations").GetInt32());
        Assert.True(preflight.GetProperty("estimate").GetProperty("promptCharacters").GetInt64() > 0);

        using var review = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.csproj",
            kind = "code",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, review.StatusCode);
        var accepted = await review.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("module", accepted.GetProperty("level").GetString());
        Assert.Equal("Sample.csproj", accepted.GetProperty("path").GetString());
        Assert.Equal(1, accepted.GetProperty("totalFiles").GetInt32());
    }

    // claude-opus-5 is priced but still unsupported by the routing policy, so the operator's
    // Claude-first attempt is refused. It must be refused as a model problem: the blanket
    // ArgumentException mapping used to report "Invalid repository path" for a perfectly valid path.
    [Fact]
    public async Task Unsupported_model_is_refused_by_name_rather_than_as_a_path_error()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "claude",
            model = "claude-opus-5",
            thinkingLevel = "max",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Invalid model selection", problem.GetProperty("title").GetString());
        var detail = problem.GetProperty("detail").GetString();
        Assert.Contains("claude-opus-5", detail, StringComparison.Ordinal);
        Assert.Contains("unsupported", detail, StringComparison.Ordinal);
    }

    // One file of code review scores into the terra-medium band; claude takes the policy's
    // provider fallback declared for that route.
    [Theory]
    [InlineData("codex", "gpt-5.6-terra", "medium")]
    [InlineData("claude", "claude-sonnet-5", "high")]
    public async Task Review_preflight_without_a_model_resolves_the_policy_default_and_says_so(
        string cliType, string expectedModel, string? expectedThinkingLevel)
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        var preflight = await estimate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(expectedModel, preflight.GetProperty("model").GetString());
        if (expectedThinkingLevel is not null)
            Assert.Equal(expectedThinkingLevel, preflight.GetProperty("thinkingLevel").GetString());
        else
            Assert.False(string.IsNullOrWhiteSpace(preflight.GetProperty("thinkingLevel").GetString()));
        Assert.Equal("policy-default", preflight.GetProperty("modelSource").GetString());
        // A default route is never an override and never asks for a below-floor confirmation.
        Assert.False(preflight.GetProperty("overrideBelowFloor").GetBoolean());
    }

    [Fact]
    public async Task Review_preflight_with_an_explicit_model_is_recorded_as_explicit()
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "codex",
            model = "gpt-5.6-sol",
            thinkingLevel = "medium",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        var preflight = await estimate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("gpt-5.6-sol", preflight.GetProperty("model").GetString());
        Assert.Equal("explicit", preflight.GetProperty("modelSource").GetString());
    }

    [Fact]
    public async Task Review_preflight_recommends_policy_route_and_start_requires_below_floor_confirmation()
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.cs",
            kind = "security",
            cliType = "codex",
            model = "gpt-5.6-luna",
            thinkingLevel = "medium",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        var preflight = await estimate.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(preflight.GetProperty("overrideBelowFloor").GetBoolean());
        Assert.Equal("gpt-5.6-sol", preflight.GetProperty("recommendation").GetProperty("recommendedModel").GetString());
        Assert.Equal("xhigh", preflight.GetProperty("recommendation").GetProperty("recommendedThinkingLevel").GetString());
        Assert.Equal("sol-xhigh", preflight.GetProperty("recommendation").GetProperty("correctnessFloor").GetString());
        Assert.Equal("model-routing-policy", preflight.GetProperty("recommendation").GetProperty("selectionSource").GetString());
        Assert.Equal(0, preflight.GetProperty("estimate").GetProperty("expectedFreshSkips").GetInt32());

        using var rejected = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "security",
            cliType = "codex",
            model = "gpt-5.6-luna",
            thinkingLevel = "medium",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var accepted = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "security",
            cliType = "codex",
            model = "gpt-5.6-luna",
            thinkingLevel = "medium",
            confirmBelowFloor = true,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var run = await accepted.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(run.GetProperty("routeOverride").GetBoolean());
        Assert.Equal("sol-xhigh", run.GetProperty("recommendation").GetProperty("correctnessFloor").GetString());
    }

    [Fact]
    public async Task Scan_returns_staleness_report()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/scan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, json.GetProperty("missingCount").GetInt32());
        var file = Assert.Single(json.GetProperty("files").EnumerateArray());
        Assert.Equal("Sample.cs", file.GetProperty("relativePath").GetString());
        Assert.Equal("missing", file.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Scope_rule_api_previews_and_atomically_manages_existing_scope_contract()
    {
        using var client = application!.CreateClient();
        using var previewResponse = await client.PostAsJsonAsync("/api/scope/rules/preview", new
        {
            action = "exclude",
            pattern = "*.cs",
            reason = "Reviewed by the generated-code pipeline.",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(preview.GetProperty("widerPattern").GetBoolean());
        Assert.Contains(preview.GetProperty("matchedFiles").EnumerateArray(), value => value.GetString() == "Sample.cs");

        using var createdResponse = await client.PostAsJsonAsync("/api/scope/rules", new
        {
            action = "exclude",
            pattern = "Sample.cs",
            reason = "Ignore this exact path in future reviews.",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var rule = Assert.Single(created.GetProperty("rules").EnumerateArray());
        Assert.Equal("Sample.cs", rule.GetProperty("pattern").GetString());
        var scopePath = Path.Combine(repositoryRoot, ".quality", "scope.json");
        Assert.True(File.Exists(scopePath));
        using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(scopePath, TestContext.Current.CancellationToken)))
        {
            Assert.Equal(RepositoryScopeConfigurationStore.Schema, persisted.RootElement.GetProperty("$schema").GetString());
        }

        using var updatedResponse = await client.PutAsJsonAsync("/api/scope/rules/0", new
        {
            action = "include",
            pattern = "Sample.cs",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("include", Assert.Single(updated.GetProperty("rules").EnumerateArray()).GetProperty("action").GetString());

        using var deletedResponse = await client.DeleteAsync("/api/scope/rules/0", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, deletedResponse.StatusCode);
        var deleted = await deletedResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Empty(deleted.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public async Task Report_returns_scorecard_sarif_and_registry_comparison()
    {
        var secondRoot = repositoryRoot + "-report-second";
        Directory.CreateDirectory(secondRoot);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "Second.cs"),
            "namespace Second; public sealed class Marker;", TestContext.Current.CancellationToken);
        await RunGitInDirectoryAsync(secondRoot, "init", "--quiet");
        try
        {
            using var client = application!.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/repos", new
            {
                id = "report-second",
                displayName = "Report second",
                rootPath = secondRoot,
                inputBudgetCharacters = 8000,
                enabledReviewKinds = new[] { "code" },
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var response = await client.GetAsync("/api/report", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(2, json.GetProperty("repositories").GetArrayLength());
            Assert.Equal(2, json.GetProperty("comparison").GetProperty("repositories").GetArrayLength());
            Assert.All(json.GetProperty("repositories").EnumerateArray(),
                repository => Assert.True(repository.GetProperty("scorecard").TryGetProperty("coverage", out _)));

            using var sarifResponse = await client.GetAsync(
                "/api/repos/report-second/report?format=sarif", TestContext.Current.CancellationToken);
            Assert.Equal("application/sarif+json", sarifResponse.Content.Headers.ContentType?.MediaType);
            var sarif = await sarifResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("2.1.0", sarif.GetProperty("version").GetString());
            Assert.Single(sarif.GetProperty("runs").EnumerateArray());
        }
        finally
        {
            TemporaryDirectory.Delete(secondRoot);
        }
    }

    [Fact]
    public async Task Handover_dry_run_returns_the_would_be_card()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/handover", new
        {
            findingSummary = "Avoid repeated work",
            filePath = "Sample.cs",
            findingText = "Cache the repeated operation.",
            reviewKind = "performance",
            metaReference = ".quality/reviews/sample.review-meta.performance.json#repeated-work",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(json.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Fix: Avoid repeated work in Sample.cs", json.GetProperty("card").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Inputs_lists_resolved_project_inputs_for_each_kind()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/inputs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var code = json.GetProperty("kinds").GetProperty("code").GetProperty("inputs").EnumerateArray().ToArray();
        var input = Assert.Single(code, entry => entry.GetProperty("scope").GetString() == "project");
        Assert.Equal("sample-rules", input.GetProperty("id").GetString());
        // The built-in rule library resolves alongside the repository's own guidelines.
        Assert.Contains(code, entry => entry.GetProperty("scope").GetString() == "built-in" &&
            entry.GetProperty("id").GetString()!.StartsWith("QS-", StringComparison.Ordinal));
        var security = json.GetProperty("kinds").GetProperty("security").GetProperty("inputs").EnumerateArray();
        Assert.DoesNotContain(security, entry => entry.GetProperty("scope").GetString() == "project");
    }

    [Fact]
    public async Task Rules_returns_the_resolved_catalogue_with_a_trace_per_rule()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/rules", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Matches(@"^\d+\.\d+\.\d+$", json.GetProperty("catalogueVersion").GetString()!);
        Assert.Equal("built-in", Assert.Single(json.GetProperty("sources").EnumerateArray()).GetString());
        var rules = json.GetProperty("rules").EnumerateArray().ToArray();
        var traces = json.GetProperty("traces").EnumerateArray().ToArray();
        Assert.NotEmpty(rules);
        Assert.Equal(rules.Length, traces.Length);
        var rule = Assert.Single(rules, entry => entry.GetProperty("id").GetString() == "QS-CS-003");
        Assert.Equal("dotnet", rule.GetProperty("technology").GetString());
        Assert.True(rule.GetProperty("enabled").GetBoolean());
        Assert.NotEmpty(rule.GetProperty("detection").GetString()!);
        Assert.NotEmpty(rule.GetProperty("goodExample").GetString()!);
        var trace = Assert.Single(traces, entry => entry.GetProperty("id").GetString() == "QS-CS-003");
        Assert.Equal("built-in", trace.GetProperty("source").GetString());
        Assert.False(trace.GetProperty("severityOverridden").GetBoolean());
        Assert.Contains(trace.GetProperty("adapters").EnumerateArray(), value => value.GetString() == "dotnet");
        Assert.DoesNotContain(trace.GetProperty("adapters").EnumerateArray(), value => value.GetString() == "angular");
    }

    [Fact]
    public async Task Rules_filters_by_kind_and_adapter_and_rejects_an_unknown_kind()
    {
        using var client = application!.CreateClient();

        using var filtered = await client.GetAsync("/api/rules?kind=code&adapter=angular", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var json = await filtered.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var rules = json.GetProperty("rules").EnumerateArray().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.DoesNotContain("dotnet", rule.GetProperty("technology").GetString()!, StringComparison.Ordinal));
        Assert.All(rules, rule => Assert.Contains(rule.GetProperty("kinds").EnumerateArray(),
            value => value.GetString() == "code"));

        using var rejected = await client.GetAsync("/api/rules?kind=accessibility", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Rules_reports_a_repository_override_as_its_scope_without_leaking_the_path()
    {
        var overrides = Path.Combine(repositoryRoot, ".quality", "rules");
        Directory.CreateDirectory(overrides);
        await File.WriteAllTextAsync(Path.Combine(overrides, "overrides.json"),
            """
            {
              "schemaVersion": 1,
              "overrides": [
                { "id": "QS-CS-004", "severity": "critical", "reason": "Test drift caused two regressions." }
              ]
            }
            """, TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/rules", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(repositoryRoot, body, StringComparison.OrdinalIgnoreCase);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Contains(json.GetProperty("sources").EnumerateArray(), value => value.GetString() == "project");
        var trace = Assert.Single(json.GetProperty("traces").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "QS-CS-004");
        Assert.Equal("project", trace.GetProperty("source").GetString());
        Assert.True(trace.GetProperty("severityOverridden").GetBoolean());
        Assert.Equal("Test drift caused two regressions.", trace.GetProperty("reason").GetString());
        var rule = Assert.Single(json.GetProperty("rules").EnumerateArray(),
            entry => entry.GetProperty("id").GetString() == "QS-CS-004");
        Assert.Equal("critical", rule.GetProperty("severity").GetString());
        Assert.Equal("low", rule.GetProperty("authoredSeverity").GetString());
        File.Delete(Path.Combine(overrides, "overrides.json"));
    }

    [Fact]
    public async Task Guideline_authoring_endpoint_writes_a_resolver_compatible_repository_file()
    {
        using var client = application!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/guidelines", new
        {
            id = "ui-created-rule",
            enabled = true,
            priority = 90,
            kinds = new[] { "code" },
            levels = new[] { "file" },
            content = "Prefer immutable values.",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var path = Path.Combine(repositoryRoot, ".quality", "inputs", "ui-created-rule.md");
        Assert.True(File.Exists(path));
        Assert.Contains("enabled: true", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        using var inputsResponse = await client.GetAsync("/api/inputs", TestContext.Current.CancellationToken);
        var inputs = await inputsResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains(inputs.GetProperty("kinds").GetProperty("code").GetProperty("inputs").EnumerateArray(),
            input => input.GetProperty("id").GetString() == "ui-created-rule");
    }

    [Fact]
    public async Task Security_scan_returns_redacted_scan_summary()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/security/scan", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("block", json.GetProperty("verdict").GetString());
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal("gitleaks", json.GetProperty("scanner").GetString());
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());
        Assert.Equal("test-rule", finding.GetProperty("ruleId").GetString());
        Assert.Equal("Gitleaks detected a potential secret in Sample.cs at lines 1-1.", finding.GetProperty("description").GetString());
        Assert.Equal("Rotate the credential and remove the token from the repository.", finding.GetProperty("recommendation").GetString());
        Assert.Equal("Sample.cs", finding.GetProperty("path").GetString());
        Assert.False(finding.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Attack_coverage_api_exposes_complete_cells_and_appends_judgements()
    {
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Coverage.cs"), """
            var app = WebApplication.Create();
            app.MapGet("/api/coverage", () => Results.Ok());
            app.Run();
            """, TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();

        using var response = await client.GetAsync("/api/security/attack-coverage?path=",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matrix = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(matrix.GetProperty("cellCount").GetInt32() > 0);
        var row = Assert.Single(matrix.GetProperty("rows").EnumerateArray(), candidate =>
            candidate.GetProperty("boundary").GetProperty("name").GetString() == "GET /api/coverage");
        var cells = row.GetProperty("cells").EnumerateArray().ToArray();
        Assert.NotEmpty(cells);
        Assert.All(cells, cell => Assert.True(cell.TryGetProperty("verdict", out _)));
        Assert.All(cells.Where(cell => cell.GetProperty("verdict").GetString() != "notYetChecked"),
            cell => Assert.NotEmpty(cell.GetProperty("provenance").EnumerateArray()));
        var deferred = cells.First(cell => cell.GetProperty("verdict").GetString() == "notYetChecked");

        using var created = await client.PostAsJsonAsync(
            "/api/security/attack-coverage/judgements?path=",
            new
            {
                assessmentId = "api-acceptance",
                boundaryId = row.GetProperty("boundary").GetProperty("id").GetString(),
                attackId = deferred.GetProperty("attackId").GetString(),
                verdict = "pass",
                reasoning = "The test supplied positive evidence for the exact boundary input.",
                evidence = new[] { new { kind = "test", reference = "Coverage.cs", summary = "Acceptance evidence." } },
                deterministicSensorInput = Array.Empty<string>(),
                source = "agent",
                reviewer = new { agent = "api-test", model = "fixture-model", thinkingLevel = "high" },
                tokenCost = new { inputTokens = 20, outputTokens = 10, cachedInputTokens = 0, reasoningOutputTokens = 5 },
                commit = "test-commit",
                commitRange = "base..test-commit",
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(File.Exists(Path.Combine(repositoryRoot, AttackCoverageLedger.RelativePath)));
        var observation = await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("api-test", observation.GetProperty("reviewer").GetProperty("agent").GetString());
        Assert.Equal("fixture-model", observation.GetProperty("reviewer").GetProperty("model").GetString());
        Assert.Equal("high", observation.GetProperty("reviewer").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task Sensors_list_enablement_availability_and_versions()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/sensors", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(3, json.GetProperty("sensors").GetArrayLength());
        var dependency = Assert.Single(json.GetProperty("sensors").EnumerateArray(),
            sensor => sensor.GetProperty("id").GetString() == "dependencies");
        Assert.Equal("1.0.0", dependency.GetProperty("version").GetString());
        Assert.True(dependency.GetProperty("enabled").GetBoolean());
        Assert.True(dependency.GetProperty("available").GetBoolean());
        Assert.Contains("path", dependency.GetProperty("scopes").EnumerateArray().Select(scope => scope.GetString()));
        var boundaries = Assert.Single(json.GetProperty("sensors").EnumerateArray(),
            sensor => sensor.GetProperty("id").GetString() == "boundaries");
        Assert.True(boundaries.GetProperty("enabled").GetBoolean());
        Assert.True(boundaries.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Boundary_sensor_scan_persists_repository_owned_inventory()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsync("/api/sensors/boundaries/scan", null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var path = Path.Combine(repositoryRoot, BoundaryInventorySensor.InventoryRelativePath);
        Assert.True(File.Exists(path));
        using var inventory = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(1, inventory.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("boundaries", inventory.RootElement.GetProperty("sensor").GetString());
    }

    [Fact]
    public async Task Dependency_sensor_scan_returns_normalized_findings_and_provenance()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsync("/api/sensors/dependencies/scan", null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal("dependencies", json.GetProperty("provenance").GetProperty("sensorId").GetString());
        var finding = Assert.Single(json.GetProperty("findings").EnumerateArray());
        Assert.Equal("GHSA-test-advisory", finding.GetProperty("ruleId").GetString());
        Assert.Equal("high", finding.GetProperty("severity").GetString());
        Assert.Contains("fixedVersion", finding.GetProperty("evidence").GetString());
    }

    [Fact]
    public async Task Health_returns_ok_for_the_dev_launcher()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("QualityStudio.Api", json.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Finding_state_action_projects_state_and_rejects_a_conflicting_write()
    {
        var fingerprint = "sha256:" + new string('d', 64);
        var findingId = "finding-" + new string('d', 64);
        var metadataDirectory = Path.Combine(repositoryRoot, ".quality", "reviews", "files");
        Directory.CreateDirectory(metadataDirectory);
        var metadataPath = Path.Combine(metadataDirectory, "file.test.review-meta.code.json");
        var grade = new ReviewGrade(60, GradeBand.D, "One finding.");
        var metadata = new ReviewMetaDocument
        {
            Unit = new ReviewUnit("qs-v1/generic/file/" + new string('a', 64), ReviewAdapter.Generic,
                ReviewLevel.File, "Sample.cs", "Sample.cs"),
            ReviewedAt = new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero),
            Kind = ReviewKind.Code,
            Reviewer = new ReviewerIdentity("test", "test"),
            ReviewedHash = ManifestHash.Subject(new string('b', 64)),
            SubjectInputs = [new SubjectInputHash("Sample.cs", "file", "sha256:" + new string('c', 64))],
            ReviewInputs = new ReviewInputs(
                ManifestHash.ReviewInput(new string('e', 64)), true, [], [],
                new PromptReference("file-code-review", "1.0.0", "sha256:" + new string('f', 64))),
            Grade = grade,
            Summary = "One finding.",
            Aspects = [new ReviewAspect("correctness", "Correctness", grade)],
            Findings = [new ReviewFinding(findingId, "correctness", FindingSeverity.High, "Test finding",
                "A finding used by the API test.", "Review it.", [new FindingLocation("Sample.cs")],
                fingerprint, "correctness.test")],
        };
        await File.WriteAllTextAsync(
            metadataPath, ReviewMetaJson.Serialize(metadata), TestContext.Current.CancellationToken);
        var identity = new FindingIdentityRecord(fingerprint, findingId, "Sample.cs", "correctness.test");
        var store = new FindingStateStore(repositoryRoot);
        var state = (await store.MergeReviewAsync([identity], [], "test", TestContext.Current.CancellationToken))[fingerprint];

        try
        {
            using var client = application!.CreateClient();
            var before = await client.GetFromJsonAsync<JsonElement>("/api/file?path=Sample.cs", TestContext.Current.CancellationToken);
            var finding = Assert.Single(Assert.Single(before.GetProperty("metaDocuments").EnumerateArray())
                .GetProperty("findings").EnumerateArray());
            Assert.Equal("open", finding.GetProperty("state").GetString());

            using var acceptedResponse = await client.PostAsJsonAsync("/api/findings/state", new
            {
                path = "Sample.cs",
                kind = "code",
                fingerprint,
                state = "accepted",
                author = "Ada",
                reason = "Risk is understood and tracked.",
                expectedTimestamp = state.Timestamp,
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);

            var after = await client.GetFromJsonAsync<JsonElement>("/api/file?path=Sample.cs", TestContext.Current.CancellationToken);
            var projected = Assert.Single(Assert.Single(after.GetProperty("metaDocuments").EnumerateArray())
                .GetProperty("findings").EnumerateArray());
            Assert.Equal("accepted", projected.GetProperty("state").GetString());
            Assert.Equal("Ada", projected.GetProperty("stateAuthor").GetString());

            using var conflict = await client.PostAsJsonAsync("/api/findings/state", new
            {
                path = "Sample.cs",
                kind = "code",
                fingerprint,
                state = "waived",
                author = "Grace",
                reason = "A conflicting decision.",
                expectedTimestamp = state.Timestamp,
            }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally
        {
            File.Delete(metadataPath);
        }
    }

    [Fact]
    public async Task Usage_returns_filtered_ledger_aggregates_and_recent_entries()
    {
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        await UsageLedger.AppendAsync(repositoryRoot, new ReviewUsageEntry("usage-api-run", timestamp, "gpt-5", "codex",
            new TokenUsage(200, 50, 80, 10, 2400), "performance", "file", "Sample.cs",
            "review-api-sweep", 2), TestContext.Current.CancellationToken);

        using var client = application!.CreateClient();
        var since = Uri.EscapeDataString(timestamp.AddMinutes(-1).ToString("O"));
        using var response = await client.GetAsync($"/api/usage?since={since}&kind=performance", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, json.GetProperty("runs").GetInt32());
        Assert.Equal(200, json.GetProperty("inputTokens").GetInt64());
        Assert.Equal("gpt-5", Assert.Single(json.GetProperty("byModel").EnumerateArray()).GetProperty("key").GetString());
        Assert.Equal("review-api-sweep", Assert.Single(json.GetProperty("byReviewRun").EnumerateArray()).GetProperty("key").GetString());
        var recent = Assert.Single(json.GetProperty("recent").EnumerateArray());
        Assert.Equal("usage-api-run", recent.GetProperty("runId").GetString());
        Assert.Equal("review-api-sweep", recent.GetProperty("reviewRunId").GetString());
    }

    [Fact]
    public async Task Quotas_returns_a_clean_empty_report_when_no_provider_data_is_available()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/quotas", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Empty(json.GetProperty("providers").EnumerateArray());
        Assert.True(json.GetProperty("ttlSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Models_returns_the_governed_picker_catalog_with_non_routable_statuses()
    {
        using var client = application!.CreateClient();
        using var response = await client.GetAsync("/api/models", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("2026-07-24", json.GetProperty("policyVersion").GetString());
        var models = json.GetProperty("models").EnumerateArray().ToArray();
        var sol = Assert.Single(models, model => model.GetProperty("modelId").GetString() == "gpt-5.6-sol");
        Assert.Equal("frontier", sol.GetProperty("capabilityTier").GetString());
        Assert.True(sol.GetProperty("availableForNewRuns").GetBoolean());
        var retired = Assert.Single(models, model => model.GetProperty("modelId").GetString() == "claude-opus-4-1");
        Assert.Equal("deprecated", retired.GetProperty("routingStatus").GetString());
        Assert.False(retired.GetProperty("availableForNewRuns").GetBoolean());
    }

    [Fact]
    public async Task Review_endpoint_queues_and_reports_per_file_failure_without_blocking()
    {
        using var client = application!.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs",
            kind = "code",
            cliType = "adapter-that-does-not-exist",
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var id = accepted.GetProperty("id").GetString()!;
        Assert.Equal(1, accepted.GetProperty("totalFiles").GetInt32());
        var runDirectory = Path.Combine(repositoryRoot, ".quality", "runs", id);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(runDirectory, "manifest.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("Sample.cs", manifest.RootElement.GetProperty("node").GetProperty("path").GetString());
            var target = Assert.Single(manifest.RootElement.GetProperty("targets").EnumerateArray());
            Assert.Equal(
                await ReviewSubjectHasher.ComputeFileContentHashAsync(
                    Path.Combine(repositoryRoot, "Sample.cs"), TestContext.Current.CancellationToken),
                target.GetProperty("subjectHash").GetString());
        }
        Assert.True(File.Exists(Path.Combine(runDirectory, "progress.jsonl")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "status.json")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "result.json")));

        JsonElement run = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            run = await client.GetFromJsonAsync<JsonElement>($"/api/review/runs/{id}", TestContext.Current.CancellationToken);
            if (run.GetProperty("state").GetString() == "done") break;
        }

        Assert.Equal("done", run.GetProperty("state").GetString());
        Assert.Equal(1, run.GetProperty("failedFiles").GetInt32());
        Assert.Equal("failed", Assert.Single(run.GetProperty("files").EnumerateArray()).GetProperty("state").GetString());
        using (var result = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(runDirectory, "result.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("runner-default", result.RootElement.GetProperty("model").GetString());
            Assert.Equal("model-default", result.RootElement.GetProperty("thinkingLevel").GetString());
            Assert.Equal("adapter-that-does-not-exist", result.RootElement.GetProperty("cli").GetString());
        }
        var list = await client.GetFromJsonAsync<JsonElement>("/api/review/runs", TestContext.Current.CancellationToken);
        Assert.Contains(list.GetProperty("runs").EnumerateArray(), candidate => candidate.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Compare_aligns_two_run_outcomes_by_fingerprint_and_flags_route_changes()
    {
        SeedRunOutcome("run-baseline", "gpt-5.6-sol", ("sha256:" + new string('1', 64), "open"), ("sha256:" + new string('2', 64), "open"));
        SeedRunOutcome("run-candidate", "claude-opus-4-8", ("sha256:" + new string('1', 64), "open"), ("sha256:" + new string('3', 64), "open"));
        using var client = application!.CreateClient();

        using var response = await client.GetAsync(
            "/api/review/runs/compare?baselineId=run-baseline&candidateId=run-candidate", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("available", body.GetProperty("status").GetString());
        Assert.Equal("found", body.GetProperty("baseline").GetProperty("status").GetString());
        var comparison = body.GetProperty("comparison");
        Assert.False(comparison.GetProperty("route").GetProperty("compatible").GetBoolean());
        Assert.Contains(comparison.GetProperty("route").GetProperty("differences").EnumerateArray(),
            reason => reason.GetString()!.Contains("Model changed", StringComparison.Ordinal));
        Assert.Single(comparison.GetProperty("new").EnumerateArray());
        Assert.Single(comparison.GetProperty("unchanged").EnumerateArray());
        Assert.Single(comparison.GetProperty("resolved").EnumerateArray());
    }

    [Fact]
    public async Task Compare_reports_a_missing_snapshot_plainly_instead_of_failing()
    {
        SeedRunOutcome("run-only-baseline", "gpt-5.6-sol", ("sha256:" + new string('1', 64), "open"));
        using var client = application!.CreateClient();

        using var response = await client.GetAsync(
            "/api/review/runs/compare?baselineId=run-only-baseline&candidateId=run-does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Equal("missing", body.GetProperty("candidate").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("comparison").ValueKind);
    }

    [Fact]
    public async Task Pin_marks_a_run_as_a_durable_baseline_that_retention_reports_and_can_release()
    {
        SeedRunOutcome("run-pin-target", "gpt-5.6-sol", ("sha256:" + new string('1', 64), "open"));
        using var client = application!.CreateClient();

        using var pinResponse = await client.PostAsync(
            "/api/review/runs/run-pin-target/pin", content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, pinResponse.StatusCode);
        var pinned = await pinResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains(pinned.GetProperty("pinnedRunIds").EnumerateArray(), id => id.GetString() == "run-pin-target");

        var retention = await client.GetFromJsonAsync<JsonElement>(
            "/api/review/runs/retention", TestContext.Current.CancellationToken);
        Assert.Equal(1, retention.GetProperty("pinnedCount").GetInt32());
        Assert.Equal(1, retention.GetProperty("snapshotCount").GetInt32());

        using var unpinResponse = await client.DeleteAsync(
            "/api/review/runs/run-pin-target/pin", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unpinResponse.StatusCode);
        var unpinned = await unpinResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(unpinned.GetProperty("pinnedRunIds").EnumerateArray(), id => id.GetString() == "run-pin-target");
    }

    [Fact]
    public async Task Pin_rejects_a_run_id_with_no_stored_outcome()
    {
        using var client = application!.CreateClient();

        using var response = await client.PostAsync(
            "/api/review/runs/does-not-exist/pin", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private void SeedRunOutcome(string runId, string model, params (string Fingerprint, string State)[] findings)
    {
        var qualityFindings = findings.Select((finding, index) => new QualityRunFinding(
            $"finding-{index}", $"quality.rule.{index}", "correctness", "high", finding.State,
            $"Finding {index}", $"Description {index}", $"Recommendation {index}", null,
            finding.Fingerprint, [new QualityFindingLocation("src/App.cs", index + 1, 1, index + 1, 8)],
            "agent", null, null)).ToArray();
        var run = new QualityRunIdentity(
            runId, 1, RepositoryRegistry.DefaultRepositoryId, "Fixture repository", "code", "unit-project", "project", ".",
            "done", "complete",
            new DateTimeOffset(2026, 8, 11, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 1, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 11, 8, 2, 0, TimeSpan.Zero),
            model, "xhigh", "codex", false);
        var target = new QualityRunSubjectTarget("unit-file", "App.cs", "src/App.cs", "sha256:" + new string('a', 64));
        var subject = new QualityRunSubject(QualityRunReportJson.SubjectManifestHash([target]), [target]);
        var report = new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            run,
            subject,
            new QualityRunExecution(1, 0, 0, 0, 0, "done", [],
                new QualityRunUsage(1, 100, 25, 10, 5, 1200, 0.01m, "USD", "priced", null, null, null),
                new QualityRunCap(null, null, "not-configured", null), null),
            [new QualityRunObservation(
                "unit-project", "project", ".", "done", true,
                ".quality/reviews/projects/root.review-meta.code.json", "sha256:" + new string('b', 64),
                run.FinishedAt, "sha256:" + new string('c', 64), "provider-run",
                new QualityRunGrade(85, "B", "Fixture grade."), "Fixture summary.", qualityFindings)],
            new QualityRunDelta("unavailable", null, "No prior comparable run snapshot exists.", [], [], [], []),
            new QualityRunSummary(
                85, "B",
                new QualityRunFindingCounts(qualityFindings.Length,
                    new Dictionary<string, int> { ["critical"] = 0, ["high"] = qualityFindings.Length, ["medium"] = 0, ["low"] = 0, ["info"] = 0 },
                    new Dictionary<string, int> { ["open"] = qualityFindings.Length }),
                qualityFindings.Length > 0 ? "high" : null, null));
        new QualityRunReportStore(repositoryRoot).Save(report);
    }

    [Fact]
    public async Task Registry_onboards_and_scopes_a_second_repository()
    {
        var secondRoot = repositoryRoot + "-second";
        Directory.CreateDirectory(secondRoot);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "Second.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(secondRoot, "Second.cs"), "namespace Second; public sealed class Marker;", TestContext.Current.CancellationToken);
        await RunGitInDirectoryAsync(secondRoot, "init", "--quiet");

        try
        {
            using var client = application!.CreateClient();
            var create = await client.PostAsJsonAsync("/api/repos", new
            {
                id = "second",
                displayName = "Second repository",
                rootPath = secondRoot,
                globalInputsDirectory = (string?)null,
                inputBudgetCharacters = 8000,
                enabledReviewKinds = new[] { "code", "security" },
                sensors = new object[]
                {
                    new { id = "gitleaks", enabled = true },
                    new { id = "dependencies", enabled = false, configuration = new { ecosystems = "npm" } },
                },
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var scopedFile = await client.GetAsync("/api/repos/second/file?path=Second.cs", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, scopedFile.StatusCode);
            var file = await scopedFile.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Contains("namespace Second", file.GetProperty("content").GetString());

            using var sensors = await client.GetAsync("/api/repos/second/sensors", TestContext.Current.CancellationToken);
            var sensorsJson = await sensors.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var dependency = Assert.Single(sensorsJson.GetProperty("sensors").EnumerateArray(),
                sensor => sensor.GetProperty("id").GetString() == "dependencies");
            Assert.False(dependency.GetProperty("enabled").GetBoolean());
            Assert.Equal("npm", dependency.GetProperty("configuration").GetProperty("ecosystems").GetString());

            using var traversal = await client.GetAsync($"/api/file?path=../{Path.GetFileName(secondRoot)}/Second.cs", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);

            var persisted = await File.ReadAllTextAsync(Path.Combine(hostRoot, ".quality-studio", "repositories.json"), TestContext.Current.CancellationToken);
            Assert.Contains("Second repository", persisted);
            Assert.Contains("ecosystems", persisted);
        }
        finally
        {
            TemporaryDirectory.Delete(secondRoot);
        }
    }

    [Fact]
    public async Task Registry_rejects_a_directory_that_is_not_a_git_repository()
    {
        var invalidRoot = repositoryRoot + "-not-git";
        Directory.CreateDirectory(invalidRoot);
        try
        {
            using var client = application!.CreateClient();
            var response = await client.PostAsJsonAsync("/api/repos", new
            {
                displayName = "Invalid repository",
                rootPath = invalidRoot,
                inputBudgetCharacters = 12000,
                enabledReviewKinds = new[] { "code" },
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("Repository path is not a Git repository", problem.GetProperty("title").GetString());
            Assert.False(problem.TryGetProperty("detail", out _));
        }
        finally
        {
            TemporaryDirectory.Delete(invalidRoot);
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(repositoryRoot);
        Directory.CreateDirectory(hostRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "Sample.cs"), "namespace Sample; public static class Greeter { public static string Hello() => \"hello\"; }");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "bin", "Generated.cs"), "namespace Generated; internal sealed class Output { }");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "generated"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "generated", "Generated.cs"), "namespace Generated; internal sealed class Local { }");
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".gitignore"), "bin/\ngenerated/\n");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".quality", "inputs"));
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".quality", "inputs", "sample.md"),
            "---\nid: sample-rules\nkinds: [code]\nlevels: [file]\npriority: 10\n---\nPrefer explicit names.\n");
        await RunGitAsync("init", "--quiet");
        application = new TestApplication(repositoryRoot, hostRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (application is not null)
        {
            await application.DisposeAsync();
        }

        TemporaryDirectory.Delete(repositoryRoot);
        TemporaryDirectory.Delete(hostRoot);
    }

    private async Task RunGitAsync(params string[] arguments)
    {
        await RunGitInDirectoryAsync(repositoryRoot, arguments);
    }

    private static IEnumerable<JsonElement> FlattenTree(JsonElement nodes)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            yield return node;
            foreach (var child in FlattenTree(node.GetProperty("children"))) yield return child;
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
                services.AddSingleton<GitleaksSecurityScanner, FakeSecurityScanner>();
                services.RemoveAll<IReviewSensor>();
                services.AddSingleton<IReviewSensor>(serviceProvider => serviceProvider.GetRequiredService<GitleaksSecurityScanner>());
                services.AddSingleton<IReviewSensor, FakeDependencySensor>();
                services.AddSingleton<IReviewSensor, BoundaryInventorySensor>();
            });
        }
    }

    private sealed class FakeSecurityScanner : GitleaksSecurityScanner
    {
        public FakeSecurityScanner() : base(null, null) { }

        public override Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true,
                ToolVersions: new Dictionary<string, string> { ["gitleaks"] = "8.24.2" }));

        public override Task<SecurityScanResult> ScanAsync(SecurityScanRequest request, CancellationToken cancellationToken = default)
        {
            var finding = new SecurityFindingRecord(
                "gitleaks-secret-1",
                "secrets",
                FindingSeverity.High,
                "Hard-coded token",
                "Gitleaks detected a potential secret in Sample.cs at lines 1-1.",
                "Rotate the credential and remove the token from the repository.",
                [new FindingLocation("Sample.cs", new FindingRange(new FindingPosition(1, 1), new FindingPosition(1, 12)))],
                "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "test-rule",
                null,
                "Sample.cs",
                Accepted: false);

            var scannedAt = DateTime.UtcNow.ToString("O");
            var report = new SecurityScanReport(
                SecurityVerdict.Block,
                true,
                "gitleaks",
                "8.24.2",
                "repository",
                null,
                null,
                null,
                scannedAt,
                1,
                1,
                0,
                1,
                0,
                0,
                null,
                [finding]);

            var provenance = new SecurityScanProvenance("gitleaks", "8.24.2", "repository", null, null, null, scannedAt);
            var counts = new SecurityScanCounts(1, 1, 0, 1, 0, 0);
            return Task.FromResult(new SecurityScanResult(report, provenance, counts, [finding]));
        }
    }

    private sealed class FakeDependencySensor : IReviewSensor
    {
        public string Id => "dependencies";

        public string Version => "1.0.0";

        public IReadOnlyList<SensorScope> SupportedScopes { get; } = [SensorScope.Repository, SensorScope.Path];

        public Task<SensorAvailability> ProbeAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorAvailability(true, ToolVersions: new Dictionary<string, string> { ["npm"] = "11.4.2" }));

        public Task<SensorScanResult> RunAsync(SensorScanRequest request, CancellationToken cancellationToken = default)
        {
            var finding = new ReviewFinding(
                "dependency-test",
                "dependencies",
                FindingSeverity.High,
                "Vulnerable dependency: sample 1.0.0",
                "sample 1.0.0 is affected by advisory GHSA-test-advisory.",
                "Upgrade sample to 1.0.1.",
                [new FindingLocation("Sample.csproj")],
                "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "GHSA-test-advisory",
                "{\"package\":\"sample\",\"version\":\"1.0.0\",\"fixedVersion\":\"1.0.1\"}");
            return Task.FromResult(new SensorScanResult(true, null, [finding],
                new SensorProvenance(Id, Version, "repository", ".", DateTime.UtcNow.ToString("O"),
                    new Dictionary<string, string> { ["npm"] = "11.4.2" })));
        }
    }
}
