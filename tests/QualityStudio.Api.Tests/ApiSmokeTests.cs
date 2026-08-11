using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Quota;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        var excluded = Assert.Single(module.GetProperty("excluded").EnumerateArray());
        Assert.Equal("bin/Generated.cs", excluded.GetProperty("path").GetString());
        Assert.Contains(".gitignore:1", excluded.GetProperty("reason").GetString(), StringComparison.Ordinal);
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

    [Fact]
    public async Task Review_preflight_recommends_policy_route_and_start_requires_below_floor_confirmation()
    {
        using var client = application!.CreateClient();
        using var estimate = await client.PostAsJsonAsync("/api/review/estimate", new
        {
            path = "Sample.cs", kind = "security", cliType = "codex",
            model = "gpt-5.6-luna", thinkingLevel = "medium",
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
            path = "Sample.cs", kind = "security", cliType = "codex",
            model = "gpt-5.6-luna", thinkingLevel = "medium",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using var accepted = await client.PostAsJsonAsync("/api/review", new
        {
            path = "Sample.cs", kind = "security", cliType = "codex",
            model = "gpt-5.6-luna", thinkingLevel = "medium", confirmBelowFloor = true,
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
            action = "exclude", pattern = "*.cs", reason = "Reviewed by the generated-code pipeline.",
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(preview.GetProperty("widerPattern").GetBoolean());
        Assert.Contains(preview.GetProperty("matchedFiles").EnumerateArray(), value => value.GetString() == "Sample.cs");

        using var createdResponse = await client.PostAsJsonAsync("/api/scope/rules", new
        {
            action = "exclude", pattern = "Sample.cs", reason = "Ignore this exact path in future reviews.",
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
            action = "include", pattern = "Sample.cs",
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
            Directory.Delete(secondRoot, true);
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
        var code = json.GetProperty("kinds").GetProperty("code");
        var input = Assert.Single(code.GetProperty("inputs").EnumerateArray());
        Assert.Equal("sample-rules", input.GetProperty("id").GetString());
        Assert.Equal("project", input.GetProperty("scope").GetString());
        Assert.Empty(json.GetProperty("kinds").GetProperty("security").GetProperty("inputs").EnumerateArray());
    }

    [Fact]
    public async Task Guideline_authoring_endpoint_writes_a_resolver_compatible_repository_file()
    {
        using var client = application!.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/guidelines", new
        {
            id = "ui-created-rule", enabled = true, priority = 90,
            kinds = new[] { "code" }, levels = new[] { "file" }, content = "Prefer immutable values.",
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
        var metadata = new JsonObject
        {
            ["unit"] = new JsonObject { ["path"] = "Sample.cs" },
            ["reviewedAt"] = "2026-07-22T09:00:00.000Z",
            ["kind"] = "code",
            ["reviewer"] = new JsonObject { ["agent"] = "test", ["model"] = "test" },
            ["grade"] = new JsonObject { ["score"] = 60, ["band"] = "D", ["rationale"] = "One finding." },
            ["summary"] = "One finding.",
            ["findings"] = new JsonArray(new JsonObject
            {
                ["id"] = findingId,
                ["fingerprint"] = fingerprint,
                ["ruleId"] = "correctness.test",
                ["aspect"] = "correctness",
                ["severity"] = "high",
                ["title"] = "Test finding",
                ["description"] = "A finding used by the API test.",
                ["recommendation"] = "Review it.",
                ["locations"] = new JsonArray(new JsonObject { ["path"] = "Sample.cs" }),
            }),
        };
        await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString(), TestContext.Current.CancellationToken);
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
            Directory.Delete(secondRoot, true);
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
            Directory.Delete(invalidRoot, true);
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
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, ".gitignore"), "bin/\n");
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

        try
        {
            Directory.Delete(repositoryRoot, true);
            Directory.Delete(hostRoot, true);
        }
        catch (IOException)
        {
        }
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
        public new HttpClient CreateClient() => LocalApiClient.Create(this);

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
