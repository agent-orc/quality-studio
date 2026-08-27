using AgentOrchestrator.CodeQuality;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class RuleOverrideConfigurationStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quality-rules-config-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_returns_empty_when_no_file_exists()
    {
        Assert.Empty(new RuleOverrideConfigurationStore(root).Read());
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var store = new RuleOverrideConfigurationStore(root);
        store.Write([
            new RuleOverride("QS-NG-001", false, "Not applicable to this design system."),
            new RuleOverride("QS-CS-001", true, null),
        ]);

        var overrides = store.Read();

        Assert.Equal(2, overrides.Count);
        Assert.Contains(overrides, value => value.RuleId == "QS-NG-001" && !value.Enabled && value.Reason == "Not applicable to this design system.");
        Assert.Contains(overrides, value => value.RuleId == "QS-CS-001" && value.Enabled && value.Reason == null);
    }

    [Fact]
    public void Read_rejects_disabling_a_rule_without_a_reason()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"), """{"overrides":[{"ruleId":"QS-NG-001","enabled":false}]}""");

        Assert.Throws<InvalidDataException>(() => new RuleOverrideConfigurationStore(root).Read());
    }

    [Fact]
    public void Read_rejects_a_duplicate_rule_id()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"),
            """{"overrides":[{"ruleId":"QS-CS-001","enabled":true},{"ruleId":"QS-CS-001","enabled":false,"reason":"duplicate"}]}""");

        Assert.Throws<InvalidDataException>(() => new RuleOverrideConfigurationStore(root).Read());
    }

    [Fact]
    public void Read_rejects_an_unsupported_schema()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "rules.json"),
            """{"$schema":"https://example.invalid/other.json","overrides":[]}""");

        Assert.Throws<InvalidDataException>(() => new RuleOverrideConfigurationStore(root).Read());
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
