using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests
{
    private static readonly Lazy<JsonSchema> RuleSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "rule.v1.schema.json"))));

    private static readonly Lazy<JsonSchema> RuleConfigSchema = new(() => JsonSchema.FromText(File.ReadAllText(Path.Combine(
        RepositoryTestContext.FindRepositoryRoot(), "schemas", "rule-config.v1.schema.json"))));

    public static IEnumerable<object[]> RuleFiles() =>
        Directory.EnumerateFiles(
                Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "rules"), "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(RuleFiles))]
    public void Every_rule_file_on_disk_validates_against_the_rule_schema(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var evaluation = RuleSchema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, $"{path}: {evaluation}");
        Assert.Equal(Path.GetFileNameWithoutExtension(path), document.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void Rule_ids_are_unique_and_directory_matches_technology()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "rules"), "*.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var id = document.RootElement.GetProperty("id").GetString()!;
            var technology = document.RootElement.GetProperty("technology").GetString();
            Assert.True(seen.Add(id), $"Duplicate rule id '{id}'.");
            Assert.Equal(Path.GetFileName(Path.GetDirectoryName(path)), technology);
        }
    }

    [Fact]
    public void RuleLibrary_loads_every_file_on_disk_as_an_embedded_resource()
    {
        var onDisk = Directory.EnumerateFiles(
            Path.Combine(RepositoryTestContext.FindRepositoryRoot(), "rules"), "*.json", SearchOption.AllDirectories);

        Assert.Equal(onDisk.Count(), RuleLibrary.Rules.Count);
        Assert.Equal(RuleLibrary.Rules.Select(rule => rule.Id).OrderBy(id => id, StringComparer.Ordinal),
            RuleLibrary.Rules.Select(rule => rule.Id));
    }

    [Theory]
    [InlineData("angular", 5, 2)]
    [InlineData("dotnet", 4, 2)]
    public void Each_seed_set_covers_its_required_categories(string technology, int expectedCategories, int minimumPerCategory)
    {
        var byCategory = RuleLibrary.Rules
            .Where(rule => rule.Technology == technology)
            .GroupBy(rule => rule.Category)
            .ToArray();

        Assert.Equal(expectedCategories, byCategory.Length);
        Assert.All(byCategory, group => Assert.True(group.Count() >= minimumPerCategory,
            $"Category '{group.Key}' has only {group.Count()} rule(s)."));
    }

    [Fact]
    public void Design_token_and_component_reuse_categories_are_covered_and_default_on()
    {
        var designTokenRules = RuleLibrary.Rules.Where(rule => rule.Category == "design-tokens").ToArray();
        var reuseRules = RuleLibrary.Rules.Where(rule => rule.Category == "component-reuse").ToArray();

        Assert.NotEmpty(designTokenRules);
        Assert.NotEmpty(reuseRules);
        Assert.All(designTokenRules, rule => Assert.True(rule.DefaultOn, $"{rule.Id} should be defaultOn."));
        Assert.All(reuseRules, rule => Assert.True(rule.DefaultOn, $"{rule.Id} should be defaultOn."));
    }

    [Fact]
    public void GuidelineStore_catalogue_includes_the_legacy_entries_and_every_rule()
    {
        var catalogue = GuidelineStore.Catalogue;

        Assert.Contains(catalogue, entry => entry.Id == "dotnet-api-safety");
        Assert.Contains(catalogue, entry => entry.Id == "security-boundaries");
        foreach (var rule in RuleLibrary.Rules)
        {
            var entry = Assert.Single(catalogue, value => value.Id == rule.Id);
            Assert.Equal(rule.Technology, entry.Technology);
            Assert.Equal(rule.Statement, entry.Description);
            Assert.Contains(rule.Id, entry.Guideline.Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Rule_catalogue_entries_install_as_ordinary_repository_guidelines()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-install-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new GuidelineStore();
            var rule = RuleLibrary.Rules.First(value => value.Category == "design-tokens");

            var installed = store.Install(root, rule.Id);

            Assert.Equal(rule.Id, installed.Id);
            var reListed = store.List(root);
            Assert.Contains(reListed, value => value.Id == rule.Id);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InputResolver_applies_default_on_rules_and_project_overrides_without_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-resolution-tests", Guid.NewGuid().ToString("N"));
        var disabledRuleId = RuleLibrary.Rules.First(rule => rule.DefaultOn).Id;
        var adjustedRule = RuleLibrary.Rules.First(rule => rule.DefaultOn && rule.Id != disabledRuleId);
        var optedInRule = RuleLibrary.Rules.First(rule => !rule.DefaultOn &&
            rule.Kinds.Contains("code") && rule.Levels.Contains("file"));
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), $$"""
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": {
            "{{disabledRuleId}}": { "enabled": false, "reason": "test override" },
            "{{adjustedRule.Id}}": { "severity": "critical", "reason": "project impact is higher" },
            "{{optedInRule.Id}}": { "enabled": true, "reason": "project uses this convention" }
          }
        }
        """);
        try
        {
            var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);
            var applicableDefaultCount = RuleLibrary.Rules.Count(rule => rule.DefaultOn &&
                rule.Kinds.Contains("code") && rule.Levels.Contains("file"));

            Assert.Equal(applicableDefaultCount, resolved.Inputs.Count);
            Assert.DoesNotContain(resolved.Inputs, input => input.Id == disabledRuleId);
            Assert.Contains(resolved.Inputs, input => input.Id == optedInRule.Id);
            Assert.Contains(resolved.Omissions,
                omission => omission.Id == disabledRuleId && omission.Reason == "disabled-by-project");
            var adjusted = Assert.Single(resolved.Inputs, input => input.Id == adjustedRule.Id);
            Assert.Equal(95, adjusted.Priority);
            Assert.Contains("Severity: critical", adjusted.Content, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality", "inputs")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RuleConfig_rejects_unknown_rule_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-invalid-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), """
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": { "QS-NG-999": { "enabled": false } }
        }
        """);
        try
        {
            Assert.Throws<JsonException>(() => RuleConfig.Load(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RuleConfig_file_on_disk_validates_against_its_schema()
    {
        var sample = """
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": {
            "QS-NG-003": { "enabled": false, "reason": "handled by an external linter" }
          }
        }
        """;
        using var document = JsonDocument.Parse(sample);
        var evaluation = RuleConfigSchema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(evaluation.IsValid, evaluation.ToString());
    }

    [Fact]
    public void RuleConfig_missing_file_yields_empty_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = RuleConfig.Load(root);
            Assert.Empty(config.Overrides);
            Assert.True(config.IsEnabled("QS-NG-003", defaultOn: true));
            Assert.False(config.IsEnabled("QS-NG-002", defaultOn: false));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
