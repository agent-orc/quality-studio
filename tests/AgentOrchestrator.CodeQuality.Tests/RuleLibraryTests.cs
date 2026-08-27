using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleLibraryTests
{
    [Fact]
    public void Default_loads_every_embedded_seed_rule_with_unique_ids()
    {
        var ids = RuleLibrary.Default.Rules.Select(rule => rule.Id).ToArray();

        Assert.Equal(9, ids.Length);
        Assert.Equal(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), ids.Length);
        Assert.All(ids, id => Assert.Matches("^QS-(NG|CS)-[0-9]{3}$", id));
        Assert.Contains("QS-NG-001", ids);
        Assert.Contains("QS-CS-003", ids);
    }

    [Fact]
    public void Default_rules_have_non_empty_content_and_valid_severity()
    {
        Assert.All(RuleLibrary.Default.Rules, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Content));
            Assert.Contains(rule.Severity, new[] { "critical", "high", "medium", "low", "info" });
            Assert.Contains(rule.Technology, new[] { "angular", "dotnet" });
            Assert.NotEmpty(rule.Kinds);
            Assert.NotEmpty(rule.Levels);
        });
    }

    [Fact]
    public void ApplyOverrides_disables_a_default_on_rule()
    {
        var rule = Assert.Single(RuleLibrary.Default.Rules, value => value.Id == "QS-NG-001");
        Assert.True(rule.DefaultOn);

        var effective = RuleLibrary.Default.ApplyOverrides([new RuleOverride("QS-NG-001", false, "not applicable here")]);

        Assert.DoesNotContain(effective, value => value.Id == "QS-NG-001");
    }

    [Fact]
    public void ApplyOverrides_enables_an_opt_in_rule()
    {
        var rule = Assert.Single(RuleLibrary.Default.Rules, value => value.Id == "QS-CS-001");
        Assert.False(rule.DefaultOn);

        var effective = RuleLibrary.Default.ApplyOverrides([new RuleOverride("QS-CS-001", true, null)]);

        Assert.Contains(effective, value => value.Id == "QS-CS-001");
    }

    [Fact]
    public void ApplyOverrides_rejects_an_unknown_rule_id()
    {
        Assert.Throws<ArgumentException>(() =>
            RuleLibrary.Default.ApplyOverrides([new RuleOverride("QS-XX-999", false, "typo")]));
    }

    [Fact]
    public void ResolveDefaultOn_reads_project_overrides_from_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "quality-rule-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"),
            """{"overrides":[{"ruleId":"QS-NG-002","enabled":false,"reason":"Feature-folder duplication is deliberate here."}]}""");
        try
        {
            var effective = RuleLibrary.Default.ResolveDefaultOn(root);
            Assert.DoesNotContain(effective, value => value.Id == "QS-NG-002");
            Assert.Contains(effective, value => value.Id == "QS-NG-001");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Constructor_rejects_duplicate_rule_ids()
    {
        var duplicate = RuleLibrary.Default.Rules.First();
        Assert.Throws<InvalidDataException>(() => new RuleLibrary([duplicate, duplicate]));
    }
}
