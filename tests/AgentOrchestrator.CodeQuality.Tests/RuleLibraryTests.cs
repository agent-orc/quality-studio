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
            Assert.StartsWith(technology == "angular" ? "QS-NG-" : "QS-DN-", id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Grounded_example_sources_exist_in_this_repository()
    {
        var repositoryRoot = RepositoryTestContext.FindRepositoryRoot();
        foreach (var rule in RuleLibrary.Rules)
        {
            foreach (var example in new[] { rule.Good, rule.Bad }.Where(example => example.Source is not null))
            {
                var path = Path.Combine(repositoryRoot, example.Source!.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path) || Directory.Exists(path),
                    $"{rule.Id} example source does not exist: {example.Source}");
            }
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
            var rule = RuleLibrary.Rules.First(value => !value.DefaultOn);

            var installed = store.Install(root, rule.Id);
            _ = new InputResolver().Resolve(root, "code", ReviewLevel.File);

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
    public void SyncDefaultRules_installs_default_on_rules_and_respects_disable_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-sync-tests", Guid.NewGuid().ToString("N"));
        var disabledRuleId = RuleLibrary.Rules.First(rule => rule.DefaultOn).Id;
        var adjustedRuleId = RuleLibrary.Rules.First(rule => rule.DefaultOn && rule.Id != disabledRuleId).Id;
        var optedInRuleId = RuleLibrary.Rules.First(rule => !rule.DefaultOn).Id;
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), $$"""
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": {
            "{{disabledRuleId}}": { "enabled": false, "reason": "test override" },
            "{{adjustedRuleId}}": { "severity": "high", "reason": "project risk" },
            "{{optedInRuleId}}": { "enabled": true, "reason": "project convention" }
          }
        }
        """);
        try
        {
            var store = new GuidelineStore();

            var firstSync = store.SyncDefaultRules(root);
            var defaultOnCount = RuleLibrary.Rules.Count(rule => rule.DefaultOn);
            Assert.Equal(defaultOnCount, firstSync.Count(result => result.Action == "installed"));
            var installedIds = store.List(root).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(disabledRuleId, installedIds);
            Assert.Contains(adjustedRuleId, installedIds);
            Assert.Contains(optedInRuleId, installedIds);
            var adjusted = Assert.Single(store.List(root), guideline => guideline.Id == adjustedRuleId);
            Assert.Equal(85, adjusted.Priority);
            Assert.Contains("Severity: high", adjusted.Content, StringComparison.Ordinal);

            var secondSync = store.SyncDefaultRules(root);
            Assert.All(secondSync.Where(result => result.Action != "removed"),
                result => Assert.Equal("unchanged", result.Action));
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

    [Fact]
    public void Input_resolution_automatically_materializes_the_default_on_core()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-auto-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);

            var expectedIds = RuleLibrary.Rules.Where(rule => rule.DefaultOn).Select(rule => rule.Id).ToHashSet();
            Assert.All(expectedIds, id => Assert.Contains(resolved.Inputs, input => input.Id == id));
            Assert.All(expectedIds, id => Assert.True(
                File.Exists(Path.Combine(root, ".quality", "inputs", id + ".md")), $"{id} was not synced."));

            var disabledId = expectedIds.First();
            File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), $$"""
            {
              "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
              "schemaVersion": 1,
              "overrides": {
                "{{disabledId}}": { "enabled": false, "reason": "covered elsewhere" }
              }
            }
            """);

            var overridden = new InputResolver().Resolve(root, "code", ReviewLevel.File);
            Assert.DoesNotContain(overridden.Inputs, input => input.Id == disabledId);
            Assert.False(File.Exists(Path.Combine(root, ".quality", "inputs", disabledId + ".md")));
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
          "overrides": {
            "QS-NG-999": { "enabled": false, "reason": "typo" }
          }
        }
        """);
        try
        {
            var exception = Assert.Throws<JsonException>(() => RuleConfig.Load(root));
            Assert.Contains("unknown rule 'QS-NG-999'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
