using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum RuleTier
{
    Core,
    Extended,
}

public enum RuleStatus
{
    Active,
    Deprecated,
}

public sealed record RuleEntry(
    string Id,
    string Version,
    string Title,
    string Technology,
    string Category,
    IReadOnlyList<string> Kinds,
    AttackSeverity Severity,
    bool Autofixable,
    RuleTier Tier,
    RuleStatus Status,
    string Since,
    string Statement,
    string Rationale,
    string GoodExample,
    string BadExample);

public sealed record RuleCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string CatalogueVersion,
    IReadOnlyList<RuleEntry> Entries);

public sealed record RuleOverride(
    string Id,
    bool? Enabled,
    AttackSeverity? Severity,
    string Reason);

public sealed record RulesConfigDocument(
    int SchemaVersion,
    IReadOnlyList<RuleOverride> Overrides);

public sealed record ResolvedRuleEntry(
    RuleEntry Entry,
    bool Enabled,
    AttackSeverity Severity,
    string? OverrideReason)
{
    public string Id => Entry.Id;
}

public sealed record ResolvedRuleCatalogue(
    string Version,
    IReadOnlyList<ResolvedRuleEntry> Entries)
{
    public string ToPromptMarkdown()
    {
        var active = Entries.Where(entry => entry.Enabled).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        if (active.Length == 0) return string.Empty;
        return string.Join("\n\n", active.Select(entry =>
            $"### {entry.Id}: {entry.Entry.Title} [{Severity(entry.Severity)}]\n" +
            $"{entry.Entry.Statement}\n\n" +
            $"Why: {entry.Entry.Rationale}\n\n" +
            $"Good:\n```\n{entry.Entry.GoodExample}\n```\n\n" +
            $"Bad:\n```\n{entry.Entry.BadExample}\n```"));
    }

    private static string Severity(AttackSeverity severity) => severity switch
    {
        AttackSeverity.Critical => "critical",
        AttackSeverity.High => "high",
        AttackSeverity.Medium => "medium",
        AttackSeverity.Low => "low",
        _ => "info",
    };
}
