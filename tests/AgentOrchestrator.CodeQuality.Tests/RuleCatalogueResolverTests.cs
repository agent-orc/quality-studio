using Json.Schema;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleCatalogueResolverTests
{
    [Fact]
    public async Task Built_in_catalogue_conforms_to_the_repository_schema()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        using var catalogue = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "src", "AgentOrchestrator.CodeQuality", "catalogues", "rule-catalogue.v1.json"),
            TestContext.Current.CancellationToken));
        var ruleSchema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(root, "schemas", "quality-rule.v1.schema.json"), TestContext.Current.CancellationToken));
        var catalogueSchema = JsonSchema.FromText(await File.ReadAllTextAsync(
            Path.Combine(root, "schemas", "quality-rule-catalogue.v1.schema.json"), TestContext.Current.CancellationToken));

        SchemaRegistry.Global.Register(ruleSchema);
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };

        var result = catalogueSchema.Evaluate(catalogue.RootElement, options);

        Assert.True(result.IsValid, result.ToString());
    }

    [Fact]
    public void Built_in_catalogue_has_unique_stable_ids_for_every_rule()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var resolved = new RuleCatalogueResolver().Resolve(root);

        Assert.NotEmpty(resolved.Entries);
        Assert.Equal(resolved.Entries.Count, resolved.Entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(resolved.Entries, entry => Assert.Matches("^QS-[A-Z]{2,4}-[0-9]{3}$", entry.Id));
    }

    [Fact]
    public void Core_rules_are_enabled_and_extended_rules_are_disabled_with_no_project_overrides()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();

        var resolved = new RuleCatalogueResolver().Resolve(root);

        Assert.Contains(resolved.Entries, entry => entry.Entry.Tier == RuleTier.Core && entry.Enabled);
        Assert.Contains(resolved.Entries, entry => entry.Entry.Tier == RuleTier.Extended && !entry.Enabled);
        Assert.DoesNotContain(resolved.Entries, entry => entry.Entry.Tier == RuleTier.Core && !entry.Enabled);
        Assert.DoesNotContain(resolved.Entries, entry => entry.Entry.Tier == RuleTier.Extended && entry.Enabled);
    }

    [Fact]
    public async Task Project_override_can_disable_a_core_rule_and_enable_an_extended_one()
    {
        var sourceRoot = RepositoryTestContext.FindRepositoryRoot();
        var root = Directory.CreateTempSubdirectory("quality-studio-rules-override-").FullName;
        try
        {
            var baseline = new RuleCatalogueResolver().Resolve(sourceRoot);
            var coreRule = baseline.Entries.First(entry => entry.Entry.Tier == RuleTier.Core);
            var extendedRule = baseline.Entries.First(entry => entry.Entry.Tier == RuleTier.Extended);

            await WriteOverridesAsync(root, $$"""
                {
                  "schemaVersion": 1,
                  "overrides": [
                    { "id": "{{coreRule.Id}}", "enabled": false, "reason": "Not applicable to this repository." },
                    { "id": "{{extendedRule.Id}}", "enabled": true, "reason": "Opting in for this team." }
                  ]
                }
                """);

            var resolved = new RuleCatalogueResolver().Resolve(root);

            Assert.False(resolved.Entries.Single(entry => entry.Id == coreRule.Id).Enabled);
            Assert.True(resolved.Entries.Single(entry => entry.Id == extendedRule.Id).Enabled);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Project_override_can_adjust_severity_without_changing_enabled_state()
    {
        var sourceRoot = RepositoryTestContext.FindRepositoryRoot();
        var root = Directory.CreateTempSubdirectory("quality-studio-rules-severity-").FullName;
        try
        {
            var baseline = new RuleCatalogueResolver().Resolve(sourceRoot);
            var coreRule = baseline.Entries.First(entry => entry.Entry.Tier == RuleTier.Core);

            await WriteOverridesAsync(root, $$"""
                {
                  "schemaVersion": 1,
                  "overrides": [
                    { "id": "{{coreRule.Id}}", "severity": "critical", "reason": "This team escalates it." }
                  ]
                }
                """);

            var resolved = new RuleCatalogueResolver().Resolve(root);
            var overridden = resolved.Entries.Single(entry => entry.Id == coreRule.Id);

            Assert.True(overridden.Enabled);
            Assert.Equal(AttackSeverity.Critical, overridden.Severity);
            Assert.Equal("This team escalates it.", overridden.OverrideReason);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Override_without_a_reason_is_rejected()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rules-no-reason-").FullName;
        try
        {
            await WriteOverridesAsync(root, """
                { "schemaVersion": 1, "overrides": [ { "id": "QS-NG-003", "enabled": false, "reason": "" } ] }
                """);

            Assert.Throws<JsonException>(() => new RuleCatalogueResolver().Resolve(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Override_for_an_unknown_rule_id_is_rejected()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rules-unknown-id-").FullName;
        try
        {
            await WriteOverridesAsync(root, """
                { "schemaVersion": 1, "overrides": [ { "id": "QS-NG-999", "enabled": false, "reason": "Typo id." } ] }
                """);

            Assert.Throws<JsonException>(() => new RuleCatalogueResolver().Resolve(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Override_with_neither_enabled_nor_severity_is_rejected()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-rules-empty-override-").FullName;
        try
        {
            await WriteOverridesAsync(root, """
                { "schemaVersion": 1, "overrides": [ { "id": "QS-NG-003", "reason": "No actual change requested." } ] }
                """);

            Assert.Throws<JsonException>(() => new RuleCatalogueResolver().Resolve(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Prompt_markdown_headings_carry_the_stable_rule_id_for_every_enabled_rule()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var resolved = new RuleCatalogueResolver().Resolve(root);

        var markdown = resolved.ToPromptMarkdown();

        Assert.All(resolved.Entries.Where(entry => entry.Enabled),
            entry => Assert.Contains($"### {entry.Id}: ", markdown, StringComparison.Ordinal));
        Assert.All(resolved.Entries.Where(entry => !entry.Enabled),
            entry => Assert.DoesNotContain($"### {entry.Id}: ", markdown, StringComparison.Ordinal));
    }

    private static async Task WriteOverridesAsync(string root, string json)
    {
        var path = Path.Combine(root, RuleCatalogueResolver.ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
    }
}
