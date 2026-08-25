using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleExample(string Language, string Code, string? Caption = null, string? Source = null);

public sealed record RuleChangelogEntry(string Version, string Date, string Summary);

/// <summary>
/// A single named rule from the rules/&lt;technology&gt;/&lt;id&gt;.json tree, as documented in
/// docs/concepts/rule-library.md and validated against schemas/rule.v1.schema.json.
/// </summary>
public sealed record RuleDefinition(
    string Id,
    string Technology,
    string Category,
    string Title,
    string Statement,
    string Rationale,
    string Severity,
    bool Autofixable,
    bool DefaultOn,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Levels,
    RuleExample Good,
    RuleExample Bad,
    IReadOnlyList<string> Tags,
    string Version,
    string Status = "active");

/// <summary>
/// Loads the embedded rules/**/*.json tree (linked into this assembly the same way prompts/ and
/// catalogues/ are) and exposes it both as typed rules and as catalogue entries that plug into the
/// existing GuidelineStore install/citation mechanism, so a rule's stable id becomes the guideline
/// heading a review prompt cites back in a finding's ruleId.
/// </summary>
public static class RuleLibrary
{
    private const string ResourceMarker = ".rules.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Severities = ["critical", "high", "medium", "low", "info"];

    public static IReadOnlyList<RuleDefinition> Rules { get; } = Load();

    public static IReadOnlyList<GuidelineCatalogueEntry> CatalogueEntries { get; } =
        Rules.Select(ToCatalogueEntry).ToArray();

    public static RuleDefinition Get(string id) =>
        Rules.SingleOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Rule '{id}' was not found in the rule library.");

    public static bool IsSeverity(string severity) => Severities.Contains(severity);

    public static GuidelineDraft CreateGuidelineDraft(RuleDefinition rule, RuleOverride? projectOverride = null)
    {
        var severity = projectOverride?.Severity ?? rule.Severity;
        return new GuidelineDraft(rule.Id, true, Priority(severity), rule.Kinds, rule.Levels,
            RenderContent(rule, severity, projectOverride?.Reason));
    }

    /// <summary>
    /// Compact context used when default rules are resolved directly into a prompt. The canonical
    /// rule file and materialized catalogue copy retain both examples; keeping automatic context to
    /// the directive and rationale lets the complete default core fit inside the established input
    /// budget without crowding out repository-authored guidance.
    /// </summary>
    public static GuidelineDraft CreatePromptGuidelineDraft(RuleDefinition rule, RuleOverride? projectOverride = null)
    {
        var severity = projectOverride?.Severity ?? rule.Severity;
        var builder = new StringBuilder();
        builder.Append('[').Append(rule.Id).Append("] ").Append(rule.Title.Trim());
        builder.Append("\n\n").Append(rule.Statement.Trim());
        builder.Append("\n\nWhy this rule exists: ").Append(rule.Rationale.Trim());
        AppendSeverity(builder, rule, severity, projectOverride?.Reason);
        return new GuidelineDraft(rule.Id, true, Priority(severity), rule.Kinds, rule.Levels, builder.ToString());
    }

    public static string RenderContent(RuleDefinition rule, string? effectiveSeverity = null, string? overrideReason = null)
    {
        effectiveSeverity ??= rule.Severity;
        var builder = new StringBuilder();
        builder.Append('[').Append(rule.Id).Append("] ").Append(rule.Title.Trim());
        builder.Append("\n\n").Append(rule.Statement.Trim());
        builder.Append("\n\nWhy this rule exists: ").Append(rule.Rationale.Trim());
        AppendSeverity(builder, rule, effectiveSeverity, overrideReason);
        AppendExample(builder, "Good", rule.Good);
        AppendExample(builder, "Avoid", rule.Bad);
        return builder.ToString();
    }

    private static void AppendSeverity(StringBuilder builder, RuleDefinition rule, string effectiveSeverity,
        string? overrideReason)
    {
        builder.Append("\n\nSeverity: ").Append(effectiveSeverity);
        if (!string.Equals(effectiveSeverity, rule.Severity, StringComparison.Ordinal))
            builder.Append(" (project override; library default: ").Append(rule.Severity).Append(')');
        if (!string.IsNullOrWhiteSpace(overrideReason))
            builder.Append(". Project override rationale: ").Append(overrideReason.Trim());
        builder
            .Append(". Autofixable: ").Append(rule.Autofixable ? "yes" : "no").Append('.');
    }

    private static void AppendExample(StringBuilder builder, string label, RuleExample example)
    {
        builder.Append("\n\n").Append(label).Append(':');
        if (!string.IsNullOrWhiteSpace(example.Caption)) builder.Append(' ').Append(example.Caption.Trim());
        builder.Append("\n```").Append(example.Language).Append('\n').Append(example.Code.Trim()).Append("\n```");
    }

    private static GuidelineCatalogueEntry ToCatalogueEntry(RuleDefinition rule) => new(
        rule.Id,
        rule.Title,
        rule.Technology,
        rule.Statement,
        CreateGuidelineDraft(rule));

    private static int Priority(string severity) => severity switch
    {
        "critical" => 95,
        "high" => 85,
        "medium" => 70,
        "low" => 55,
        _ => 40,
    };

    private static IReadOnlyList<RuleDefinition> Load()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal) &&
                           name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0) throw new InvalidOperationException("The rule library has no embedded rule resources.");

        var rules = new List<RuleDefinition>(names.Length);
        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Rule resource '{name}' is unavailable.");
            RuleDefinition rule;
            try
            {
                rule = JsonSerializer.Deserialize<RuleDefinition>(stream, JsonOptions)
                    ?? throw new JsonException($"Rule resource '{name}' is empty.");
            }
            catch (JsonException exception)
            {
                throw new JsonException($"Rule resource '{name}' failed to parse: {exception.Message}", exception);
            }
            Validate(rule, name);
            // tags is optional in the schema; normalize so consumers never see a null list.
            rules.Add(rule.Tags is null ? rule with { Tags = [] } : rule);
        }

        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new JsonException($"Duplicate rule id '{duplicate.Key}' in the rule library.");

        return rules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
    }

    private static void Validate(RuleDefinition rule, string source)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Technology) ||
            string.IsNullOrWhiteSpace(rule.Category) || string.IsNullOrWhiteSpace(rule.Title) ||
            string.IsNullOrWhiteSpace(rule.Statement) || string.IsNullOrWhiteSpace(rule.Rationale) ||
            string.IsNullOrWhiteSpace(rule.Version))
            throw new JsonException($"Rule resource '{source}' is missing a required field.");
        if (!Severities.Contains(rule.Severity))
            throw new JsonException($"Rule resource '{source}' has an unsupported severity '{rule.Severity}'.");
        if (rule.Kinds is not { Count: > 0 } || rule.Levels is not { Count: > 0 })
            throw new JsonException($"Rule resource '{source}' requires at least one kind and one level.");
        if (rule.Good is null || rule.Bad is null)
            throw new JsonException($"Rule resource '{source}' requires both a good and a bad example.");
        if (!source.Contains("." + rule.Id + ".json", StringComparison.Ordinal))
            throw new JsonException($"Rule resource '{source}' file name must match its id '{rule.Id}'.");
    }
}
