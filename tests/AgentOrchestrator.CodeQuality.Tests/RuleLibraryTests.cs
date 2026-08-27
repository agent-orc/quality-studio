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
        Assert.All(RuleLibrary.Rules, rule =>
        {
            var current = Assert.IsAssignableFrom<IReadOnlyList<RuleChangelogEntry>>(rule.Changelog);
            Assert.NotEmpty(current);
            Assert.Equal(rule.Version, current[0].Version);
        });
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
    public void The_default_on_core_is_exactly_the_set_documented_in_the_dossier()
    {
        // Pinned so the shipped core can't drift away from the table in
        // docs/concepts/rule-library.md#default-on-resolution. Changing this set is a deliberate act:
        // update the rule's defaultOn, this list, and the dossier table together.
        string[] expected =
        [
            "QS-DN-002", "QS-DN-004", "QS-DN-005", "QS-DN-006",
            "QS-NG-003", "QS-NG-004", "QS-NG-005", "QS-NG-006", "QS-NG-009",
        ];

        var actual = RuleLibrary.Rules.Where(rule => rule.DefaultOn)
            .Select(rule => rule.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_example_source_points_at_a_real_path_in_this_repository()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();

        foreach (var rule in RuleLibrary.Rules)
        foreach (var (label, example) in new[] { ("good", rule.Good), ("bad", rule.Bad) })
        {
            if (string.IsNullOrWhiteSpace(example.Source)) continue;
            var resolved = Path.Combine(root, example.Source.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(resolved) || Directory.Exists(resolved),
                $"{rule.Id} {label} example cites '{example.Source}', which does not exist. " +
                "A source must be a single repository-relative path, not a list.");
        }
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
    public void SyncDefaultRules_installs_default_on_rules_and_respects_disable_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-sync-tests", Guid.NewGuid().ToString("N"));
        var disabledRuleId = RuleLibrary.Rules.First(rule => rule.DefaultOn).Id;
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), $$"""
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": {
            "{{disabledRuleId}}": { "enabled": false, "reason": "test override" }
          }
        }
        """);
        try
        {
            var store = new GuidelineStore();

            var firstSync = store.SyncDefaultRules(root);
            var defaultOnCount = RuleLibrary.Rules.Count(rule => rule.DefaultOn);
            Assert.Equal(defaultOnCount - 1, firstSync.Count(result => result.Action == "installed"));
            var installedIds = store.List(root).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(disabledRuleId, installedIds);
            Assert.Contains(RuleLibrary.Rules.First(rule => rule.DefaultOn && rule.Id != disabledRuleId).Id, installedIds);

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
    public void Default_on_rules_feed_review_prompts_without_materializing_project_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-auto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);

            Assert.Contains(resolved.Inputs, input => input.Id == "QS-NG-003");
            Assert.Contains(resolved.Inputs, input => input.Id == "QS-DN-006");
            var namedContext = resolved.Guidelines("project");
            Assert.Contains("## QS-NG-003", namedContext, StringComparison.Ordinal);
            var prompt = new ReviewPromptBuilder().Build(
                "frontend/src/app/example.css", "code", projectGuidelines: namedContext);
            Assert.Contains("## QS-NG-003", prompt, StringComparison.Ordinal);
            Assert.Contains("Set every finding's `ruleId` to the exact id", prompt, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(root, ".quality", "inputs")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Project_config_can_disable_enable_and_adjust_individual_rules()
    {
        var root = Path.Combine(Path.GetTempPath(), "rule-library-override-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".quality", "inputs"));
        File.WriteAllText(Path.Combine(root, ".quality", "inputs", "QS-NG-003.md"), """
        ---
        id: QS-NG-003
        enabled: true
        kinds: [code]
        levels: [file]
        priority: 70
        ---
        Previously materialized copy that the JSON override must still disable.
        """);
        File.WriteAllText(Path.Combine(root, ".quality", "rules.config.json"), """
        {
          "$schema": "https://quality.studio/schemas/rule-config.v1.schema.json",
          "schemaVersion": 1,
          "overrides": {
            "QS-NG-003": { "enabled": false, "reason": "external linter owns this rule" },
            "QS-NG-007": { "enabled": true, "severity": "high", "reason": "migration policy" }
          }
        }
        """);
        try
        {
            var resolved = new InputResolver().Resolve(root, "code", ReviewLevel.File);

            Assert.DoesNotContain(resolved.Inputs, input => input.Id == "QS-NG-003");
            var adjusted = Assert.Single(resolved.Inputs, input => input.Id == "QS-NG-007");
            Assert.Equal(RuleLibrary.Priority("high"), adjusted.Priority);
            Assert.Contains("Severity: high", adjusted.Content, StringComparison.Ordinal);
            Assert.Contains("library default is low", adjusted.Content, StringComparison.Ordinal);
            Assert.Contains("Reason: migration policy", adjusted.Content, StringComparison.Ordinal);

            new GuidelineStore().SyncDefaultRules(root);
            var materialized = Assert.Single(new GuidelineStore().List(root), input => input.Id == "QS-NG-007");
            Assert.Equal(RuleLibrary.Priority("high"), materialized.Priority);
            Assert.Contains("Reason: migration policy", materialized.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Project_config_rejects_unknown_rule_ids()
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
            var exception = Assert.Throws<JsonException>(() => RuleConfig.Load(root));
            Assert.Contains("unknown rule id 'QS-NG-999'", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("\"status\": \"deprecated\", \"supersededBy\": \"QS-NG-002\",", true)]
    [InlineData("\"status\": \"deprecated\",", false)]
    [InlineData("\"status\": \"active\",", true)]
    public void A_deprecated_rule_must_name_its_successor(string statusFields, bool expectedValid)
    {
        // A retired id must never become a dead end -- deprecating without a successor is rejected.
        var template = File.ReadAllText(Path.Combine(
            RepositoryTestContext.FindRepositoryRoot(), "rules", "angular", "QS-NG-001.json"));
        var candidate = template.Replace("\"status\": \"active\",", statusFields, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(candidate);
        var evaluation = RuleSchema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.Equal(expectedValid, evaluation.IsValid);
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
