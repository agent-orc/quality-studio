using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed partial class ApiSmokeTests
{
    private string ProjectRuleFolder => Path.Combine(repositoryRoot, ".quality", "rules");

    [Fact]
    public async Task Rule_override_can_be_set_audited_and_removed_through_the_api()
    {
        using var client = application!.CreateClient();
        try
        {
            using var set = await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-004", new
            {
                enabled = false,
                severity = "info",
                reason = "The sample has no test project.",
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var body = await set.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(repositoryRoot, body, StringComparison.OrdinalIgnoreCase);
            var pool = JsonDocument.Parse(body).RootElement;
            var rule = Assert.Single(pool.GetProperty("rules").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "QS-CS-004");
            Assert.False(rule.GetProperty("enabled").GetBoolean());
            Assert.Equal("info", rule.GetProperty("severity").GetString());
            var trace = Assert.Single(pool.GetProperty("traces").EnumerateArray(), entry => entry.GetProperty("id").GetString() == "QS-CS-004");
            Assert.Equal("project", trace.GetProperty("source").GetString());
            var projectOverride = Assert.Single(pool.GetProperty("scopes").GetProperty("project").GetProperty("overrides").EnumerateArray());
            Assert.Equal("The sample has no test project.", projectOverride.GetProperty("reason").GetString());
            Assert.True(File.Exists(Path.Combine(ProjectRuleFolder, "overrides.json")));

            var audit = await client.GetFromJsonAsync<JsonElement>("/api/repos/default/rules/audit?scope=project",
                TestContext.Current.CancellationToken);
            var entry = Assert.Single(audit.GetProperty("entries").EnumerateArray());
            Assert.Equal("override.set", entry.GetProperty("action").GetString());
            Assert.Equal("local-development", entry.GetProperty("actor").GetString());

            using var removed = await client.DeleteAsync(
                "/api/rules/overrides/QS-CS-004?reason=Tests%20were%20added.", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            Assert.False(File.Exists(Path.Combine(ProjectRuleFolder, "overrides.json")));
            using var missing = await client.DeleteAsync(
                "/api/rules/overrides/QS-CS-004?reason=Again.", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    [Fact]
    public async Task Rule_change_that_breaks_the_pool_is_rejected_with_diagnostics()
    {
        using var client = application!.CreateClient();

        using var unknown = await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-999",
            new { enabled = false, reason = "Typo." }, TestContext.Current.CancellationToken);
        using var noReason = await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-003",
            new { enabled = false }, TestContext.Current.CancellationToken);
        using var badScope = await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-003?scope=team",
            new { enabled = false, reason = "Team." }, TestContext.Current.CancellationToken);
        using var badSeverity = await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-003",
            new { severity = "urgent", reason = "Now." }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var problem = await unknown.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains(problem.GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("message").GetString()!.Contains("QS-CS-999", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSeverity.StatusCode);
        Assert.False(Directory.Exists(ProjectRuleFolder));
    }

    [Fact]
    public async Task Custom_rule_is_validated_saved_injected_into_review_inputs_and_deleted()
    {
        using var client = application!.CreateClient();
        try
        {
            using var invalid = await client.PostAsJsonAsync("/api/rules/custom/validate",
                new { content = CustomRule().Replace("kinds: [code]", "kinds: [style]", StringComparison.Ordinal) },
                TestContext.Current.CancellationToken);
            var invalidResult = await invalid.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.False(invalidResult.GetProperty("valid").GetBoolean());
            Assert.Contains(invalidResult.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("message").GetString()!.Contains("kind 'style'", StringComparison.Ordinal));

            using var valid = await client.PostAsJsonAsync("/api/rules/custom/validate",
                new { content = CustomRule() }, TestContext.Current.CancellationToken);
            var validResult = await valid.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.True(validResult.GetProperty("valid").GetBoolean());
            Assert.Equal("project", validResult.GetProperty("rule").GetProperty("origin").GetString());
            Assert.False(Directory.Exists(ProjectRuleFolder));

            using var saved = await client.PutAsJsonAsync("/api/repos/default/rules/custom/ACME-CS-001",
                new { content = CustomRule(), reason = "Adopt structured logging." }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.True(File.Exists(Path.Combine(ProjectRuleFolder, "custom", "ACME-CS-001.md")));

            var inputs = await client.GetFromJsonAsync<JsonElement>("/api/inputs", TestContext.Current.CancellationToken);
            Assert.Contains(inputs.GetProperty("kinds").GetProperty("code").GetProperty("inputs").EnumerateArray(),
                input => input.GetProperty("id").GetString() == "ACME-CS-001");
            var pool = await client.GetFromJsonAsync<JsonElement>("/api/rules", TestContext.Current.CancellationToken);
            var custom = Assert.Single(pool.GetProperty("scopes").GetProperty("project").GetProperty("customRules").EnumerateArray());
            Assert.Equal("ACME-CS-001.md", custom.GetProperty("fileName").GetString());
            Assert.Contains("Log through the structured logger", custom.GetProperty("content").GetString(), StringComparison.Ordinal);

            using var deleted = await client.DeleteAsync("/api/rules/custom/ACME-CS-001?reason=Replaced%20by%20CA2254.",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.False(File.Exists(Path.Combine(ProjectRuleFolder, "custom", "ACME-CS-001.md")));
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    [Fact]
    public async Task Applicability_selects_packs_that_replace_the_house_style_defaults()
    {
        using var client = application!.CreateClient();
        try
        {
            var before = await client.GetFromJsonAsync<JsonElement>("/api/rules", TestContext.Current.CancellationToken);
            Assert.Equal("default", before.GetProperty("applicability").GetProperty("scope").GetString());
            Assert.Contains(before.GetProperty("packs").EnumerateArray(),
                pack => pack.GetProperty("id").GetString() == "house-style" && pack.GetProperty("selected").GetBoolean());

            using var set = await client.PutAsJsonAsync("/api/rules/applicability", new
            {
                packs = new[] { "dotnet-service", "public-website" },
                reason = "A .NET service that also serves the public product pages.",
            }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var pool = await set.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("project", pool.GetProperty("applicability").GetProperty("scope").GetString());
            var rules = pool.GetProperty("rules").EnumerateArray().ToArray();
            Assert.All(rules.Where(rule => rule.GetProperty("technology").GetString() == "angular"),
                rule => Assert.False(rule.GetProperty("enabled").GetBoolean()));
            var seo = Assert.Single(rules, rule => rule.GetProperty("id").GetString() == "QS-GN-005");
            Assert.True(seo.GetProperty("enabled").GetBoolean());
            Assert.Equal("public-website", Assert.Single(seo.GetProperty("selectedBy").EnumerateArray()).GetString());

            using var cleared = await client.DeleteAsync("/api/rules/applicability?reason=Back%20to%20defaults.",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
            var after = await cleared.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal("default", after.GetProperty("applicability").GetProperty("scope").GetString());
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    [Fact]
    public async Task Rule_set_export_and_import_round_trip_through_the_api()
    {
        using var client = application!.CreateClient();
        try
        {
            await client.PutAsJsonAsync("/api/rules/custom/ACME-CS-001",
                new { content = CustomRule(), reason = "Adopt structured logging." }, TestContext.Current.CancellationToken);
            await client.PutAsJsonAsync("/api/rules/overrides/QS-CS-003",
                new { severity = "critical", reason = "Cancellation outages." }, TestContext.Current.CancellationToken);
            await client.PutAsJsonAsync("/api/rules/applicability",
                new { packs = new[] { "dotnet-service" }, reason = "A .NET service." }, TestContext.Current.CancellationToken);

            using var export = await client.GetAsync("/api/rules/export?scope=project", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/json", export.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("attachment; filename=\"rule-set-default-",
                export.Content.Headers.ContentDisposition?.ToString() ?? export.Headers.GetValues("Content-Disposition").Single(),
                StringComparison.Ordinal);
            var ruleSet = await export.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(RuleSetDocument.SchemaId, ruleSet.GetProperty("$schema").GetString());
            Assert.Equal("ACME-CS-001", Assert.Single(ruleSet.GetProperty("customRules").EnumerateArray()).GetProperty("id").GetString());

            TemporaryDirectory.Delete(ProjectRuleFolder);
            using var preview = await client.PostAsJsonAsync("/api/rules/import?scope=project",
                new { ruleSet, mode = "replace", dryRun = true }, TestContext.Current.CancellationToken);
            var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.True(plan.GetProperty("valid").GetBoolean());
            Assert.False(plan.GetProperty("applied").GetBoolean());
            Assert.Contains(plan.GetProperty("changes").EnumerateArray(), change =>
                change.GetProperty("kind").GetString() == "custom-rule" && change.GetProperty("change").GetString() == "added");
            Assert.False(Directory.Exists(ProjectRuleFolder));

            using var applied = await client.PostAsJsonAsync("/api/rules/import?scope=project",
                new { ruleSet, mode = "replace", dryRun = false, reason = "Restore the exported set." },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
            var result = await applied.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.True(result.GetProperty("applied").GetBoolean());
            var pool = result.GetProperty("pool");
            Assert.Equal("critical", Assert.Single(pool.GetProperty("rules").EnumerateArray(),
                rule => rule.GetProperty("id").GetString() == "QS-CS-003").GetProperty("severity").GetString());
            Assert.Equal("project", pool.GetProperty("applicability").GetProperty("scope").GetString());

            using var unknownProperty = await client.PostAsJsonAsync("/api/rules/import?scope=project",
                new
                {
                    ruleSet = new
                    {
                        schemaVersion = 1,
                        overrides = Array.Empty<object>(),
                        customRules = Array.Empty<object>(),
                        packs = Array.Empty<object>(),
                        surprise = true
                    },
                    mode = "merge",
                    dryRun = true
                },
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, unknownProperty.StatusCode);
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    [Fact]
    public async Task Rule_set_import_with_a_null_custom_rule_field_answers_with_diagnostics_not_a_server_error()
    {
        using var client = application!.CreateClient();
        var rule = JsonSerializer.SerializeToNode(
            RuleMarkdown.ParseCustom(CustomRule(), "ACME-CS-001.md", new List<string>())!, AttackCoverageJson.Options)!.AsObject();
        rule["title"] = null;
        var ruleSet = new JsonObject
        {
            ["schemaVersion"] = 1, ["overrides"] = new JsonArray(), ["customRules"] = new JsonArray(rule), ["packs"] = new JsonArray(),
        };
        try
        {
            using var preview = await client.PostAsJsonAsync("/api/rules/import?scope=project",
                new { ruleSet, mode = "merge", dryRun = true }, TestContext.Current.CancellationToken);
            using var applied = await client.PostAsJsonAsync("/api/rules/import?scope=project",
                new { ruleSet, mode = "merge", dryRun = false, reason = "Apply." }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var plan = await preview.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.False(plan.GetProperty("valid").GetBoolean());
            Assert.Contains(plan.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
                diagnostic.GetProperty("message").GetString()!.Contains("'title'", StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.BadRequest, applied.StatusCode);
            var problem = await applied.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Contains(problem.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
                diagnostic.GetProperty("message").GetString()!.Contains("'title'", StringComparison.Ordinal));
            Assert.False(Directory.Exists(ProjectRuleFolder));
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    [Fact]
    public async Task A_hand_broken_rule_file_is_reported_by_the_rules_endpoint_while_reviews_fail_closed()
    {
        Directory.CreateDirectory(ProjectRuleFolder);
        await File.WriteAllTextAsync(Path.Combine(ProjectRuleFolder, "overrides.json"), """
            { "schemaVersion": 1, "overrides": [ { "id": "QS-CS-999", "enabled": false, "reason": "Typo." } ] }
            """, TestContext.Current.CancellationToken);
        using var client = application!.CreateClient();
        try
        {
            using var rules = await client.GetAsync("/api/rules", TestContext.Current.CancellationToken);
            using var inputs = await client.GetAsync("/api/inputs", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
            var body = await rules.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(repositoryRoot, body, StringComparison.OrdinalIgnoreCase);
            var pool = JsonDocument.Parse(body).RootElement;
            Assert.False(pool.GetProperty("valid").GetBoolean());
            var diagnostic = Assert.Single(pool.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(".quality/rules/overrides.json", diagnostic.GetProperty("source").GetString());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, inputs.StatusCode);
        }
        finally
        {
            TemporaryDirectory.Delete(ProjectRuleFolder);
        }
    }

    private static string CustomRule() => """
        ---
        id: ACME-CS-001
        version: 1.0.0
        title: Log through the structured logger
        technology: dotnet
        kinds: [code]
        category: logging
        severity: medium
        defaultOn: true
        autofixable: false
        deterministicRuleIds: [CA2254]
        since: 1.0.0
        ---

        ## Statement

        Write diagnostics through ILogger with message templates, never through Console.

        ## Rationale

        Console output never reaches the host's log pipeline, so an incident has no trail.

        ## Detection

        Look for Console.Write and string interpolation inside logger calls.

        ## Good example

        ```csharp
        logger.LogInformation("Review {RunId} started", runId);
        ```

        ## Bad example

        ```csharp
        Console.WriteLine($"Review {runId} started");
        ```

        ## Change history

        - 1.0.0 (2026-09-28): Initial rule.
        """;
}
