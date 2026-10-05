using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Reads and writes the authored rule format of <c>rules/&lt;technology&gt;/*.md</c> at runtime, so a
/// custom rule placed in a repository or the data root is validated and enforced without a rebuild.
/// The dialect is the one <c>scripts/sync-rule-catalogue.mjs</c> parses for the built-in library:
/// <c>key: value</c> frontmatter, six required <c>## </c> sections, fenced examples, and a
/// newest-first change history whose top version equals the frontmatter version.
/// </summary>
public static partial class RuleMarkdown
{
    /// <summary>
    /// Custom rule ids follow the built-in <c>&lt;PREFIX&gt;-&lt;TECH&gt;-&lt;NNN&gt;</c> shape with their own
    /// owner prefix. <c>QS-</c> is reserved for the built-in library, so a later library release can
    /// never collide with a rule a repository already defines.
    /// </summary>
    public const string CustomIdPattern = "^[A-Z][A-Z0-9]{1,7}-(NG|CS|GN)-[0-9]{3}$";

    /// <summary>A custom rule file above this size is rejected rather than parsed.</summary>
    public const int MaxRuleBytes = 32 * 1024;

    public static readonly IReadOnlyDictionary<string, string> TechnologyCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["angular"] = "NG",
            ["dotnet"] = "CS",
            ["generic"] = "GN",
        };

    private static readonly string[] RequiredSections =
        ["Statement", "Rationale", "Detection", "Good example", "Bad example", "Change history"];

    private static readonly string[] Kinds = ["code", "security", "performance"];

    /// <summary>
    /// Parses one custom rule. Every problem is reported, not only the first, so an author fixes a
    /// file in one pass. Returns null when any error was found.
    /// </summary>
    public static RuleDefinition? ParseCustom(string text, string fileName, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(errors);
        var before = errors.Count;
        if (Encoding.UTF8.GetByteCount(text) > MaxRuleBytes)
        {
            errors.Add($"exceeds the {MaxRuleBytes / 1024} KiB rule size limit.");
            return null;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            errors.Add("must start with '---' frontmatter.");
            return null;
        }
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            errors.Add("has unterminated frontmatter.");
            return null;
        }

        var fields = ParseFrontmatter(normalized[4..end], errors);
        var sections = ParseSections(normalized[(end + 5)..], errors);

        var id = RequireString(fields, "id", errors);
        var technology = RequireString(fields, "technology", errors);
        if (id.Length > 0)
        {
            if (id.StartsWith("QS-", StringComparison.Ordinal))
                errors.Add($"id '{id}' uses the 'QS-' prefix reserved for the built-in library; use your own prefix, e.g. 'ACME-CS-001'.");
            else if (!CustomIdRegex().IsMatch(id))
                errors.Add($"id '{id}' must match <PREFIX>-<NG|CS|GN>-<NNN>, e.g. 'ACME-CS-001'.");
            else if (TechnologyCodes.TryGetValue(technology, out var code) &&
                     !id.Contains($"-{code}-", StringComparison.Ordinal))
                errors.Add($"id '{id}' must use the '-{code}-' segment of technology '{technology}'.");
            if (!fileName.StartsWith(id, StringComparison.Ordinal))
                errors.Add($"file name must start with the rule id '{id}'.");
        }
        if (technology.Length > 0 && !TechnologyCodes.ContainsKey(technology))
            errors.Add($"technology '{technology}' must be one of angular, dotnet, generic.");

        var kinds = RequireList(fields, "kinds", errors);
        foreach (var kind in kinds.Where(kind => !Kinds.Contains(kind, StringComparer.Ordinal)))
            errors.Add($"kind '{kind}' must be one of code, security, performance.");

        var severityText = RequireString(fields, "severity", errors);
        var severity = FindingSeverity.Medium;
        if (severityText.Length > 0 && !TryParseSeverity(severityText, out severity))
            errors.Add($"severity '{severityText}' must be one of critical, high, medium, low, info.");

        var version = RequireString(fields, "version", errors);
        var history = ParseChangeHistory(sections, errors);
        if (history.Count > 0 && version.Length > 0 && history[0].Version != version)
            errors.Add($"newest change-history entry is {history[0].Version}, but version is {version}.");

        var definition = new RuleDefinition(
            id,
            version,
            RequireString(fields, "title", errors),
            technology,
            RequireString(fields, "category", errors),
            kinds,
            Collapse(Section(sections, "Statement")),
            Collapse(Section(sections, "Rationale")),
            Collapse(Section(sections, "Detection")),
            ParseExample(sections, "Good example", errors),
            ParseExample(sections, "Bad example", errors),
            severity,
            RequireBoolean(fields, "defaultOn", errors),
            RequireBoolean(fields, "autofixable", errors),
            fields.TryGetValue("deterministicRuleIds", out var mapped) && mapped is IReadOnlyList<string> list ? list : [],
            fields.TryGetValue("relatedGuideline", out var related) && related is string { Length: > 0 } guideline ? guideline : null,
            RequireString(fields, "since", errors),
            history,
            !fields.ContainsKey("enabled") || RequireBoolean(fields, "enabled", errors));
        return errors.Count == before ? definition : null;
    }

    /// <summary>
    /// Renders a rule back into the authored format. <see cref="ParseCustom"/> of the result returns
    /// an equal definition, which is what lets an imported rule set land as reviewable Markdown.
    /// </summary>
    public static string Render(RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(rule.Id).Append('\n');
        builder.Append("version: ").Append(rule.Version).Append('\n');
        builder.Append("title: ").Append(OneLine(rule.Title)).Append('\n');
        builder.Append("technology: ").Append(rule.Technology).Append('\n');
        builder.Append("kinds: [").Append(string.Join(", ", rule.Kinds)).Append("]\n");
        builder.Append("category: ").Append(OneLine(rule.Category)).Append('\n');
        builder.Append("severity: ").Append(SeverityName(rule.Severity)).Append('\n');
        builder.Append("defaultOn: ").Append(rule.DefaultOn ? "true" : "false").Append('\n');
        builder.Append("autofixable: ").Append(rule.Autofixable ? "true" : "false").Append('\n');
        builder.Append("deterministicRuleIds: [").Append(string.Join(", ", rule.DeterministicRuleIds)).Append("]\n");
        if (!string.IsNullOrWhiteSpace(rule.RelatedGuideline))
            builder.Append("relatedGuideline: ").Append(OneLine(rule.RelatedGuideline)).Append('\n');
        builder.Append("since: ").Append(rule.Since).Append('\n');
        if (!rule.Enabled) builder.Append("enabled: false\n");
        builder.Append("---\n\n");
        builder.Append("## Statement\n\n").Append(rule.Statement).Append("\n\n");
        builder.Append("## Rationale\n\n").Append(rule.Rationale).Append("\n\n");
        builder.Append("## Detection\n\n").Append(rule.Detection).Append("\n\n");
        builder.Append("## Good example\n\n```text\n").Append(rule.GoodExample).Append("\n```\n\n");
        builder.Append("## Bad example\n\n```text\n").Append(rule.BadExample).Append("\n```\n\n");
        builder.Append("## Change history\n\n");
        foreach (var entry in rule.ChangeHistory)
            builder.Append("- ").Append(entry.Version).Append(" (").Append(entry.Date).Append("): ")
                .Append(Collapse(entry.Change)).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Checks a rule that arrived as structured data (an imported rule set) against the same
    /// constraints a Markdown file must meet, by rendering and re-reading it.
    /// </summary>
    public static RuleDefinition? ValidateStructured(RuleDefinition rule, ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var before = errors.Count;
        // Structured input is user-supplied JSON: a non-nullable property can still arrive as an
        // explicit null. Report every one before anything below calls a string method on it.
        foreach (var (name, value) in new[]
                 {
                     ("id", rule.Id), ("version", rule.Version), ("title", rule.Title), ("technology", rule.Technology),
                     ("category", rule.Category), ("statement", rule.Statement), ("rationale", rule.Rationale),
                     ("detection", rule.Detection), ("goodExample", rule.GoodExample), ("badExample", rule.BadExample),
                     ("since", rule.Since),
                 })
        {
            if (value is null) errors.Add($"requires a non-empty '{name}'.");
        }
        if (rule.Kinds is null || rule.ChangeHistory is null || rule.DeterministicRuleIds is null)
            errors.Add("requires kinds, deterministicRuleIds and changeHistory.");
        foreach (var (name, values) in new[] { ("kinds", rule.Kinds), ("deterministicRuleIds", rule.DeterministicRuleIds) })
        {
            if (values?.Any(value => value is null) == true) errors.Add($"'{name}' must not contain null.");
        }
        foreach (var (entry, index) in (rule.ChangeHistory ?? []).Select((entry, index) => (entry, index)))
        {
            if (entry?.Version is null || entry.Date is null || entry.Change is null)
                errors.Add($"'changeHistory[{index}]' requires version, date and change.");
        }
        if (errors.Count != before) return null;
        foreach (var (name, value) in new[]
                 {
                     ("title", rule.Title), ("category", rule.Category), ("relatedGuideline", rule.RelatedGuideline ?? string.Empty),
                 })
        {
            if (value.Contains('\n', StringComparison.Ordinal)) errors.Add($"'{name}' must be a single line.");
        }
        foreach (var (name, value) in new[] { ("goodExample", rule.GoodExample), ("badExample", rule.BadExample) })
        {
            if (value.Contains("```", StringComparison.Ordinal)) errors.Add($"'{name}' must not contain a code fence.");
        }
        if (errors.Count != before) return null;
        return ParseCustom(Render(rule), rule.Id + ".md", errors);
    }

    /// <summary>The empty rule an author starts from; parses once its placeholders are filled.</summary>
    public static string Template(string id, string technology, string date) => $"""
        ---
        id: {id}
        version: 1.0.0
        title: One imperative sentence naming the rule
        technology: {technology}
        kinds: [code]
        category: maintainability
        severity: medium
        defaultOn: true
        autofixable: false
        deterministicRuleIds: []
        since: 1.0.0
        ---

        ## Statement

        What to do, in one or two sentences.

        ## Rationale

        Why it matters in this codebase: the concrete cost of not doing it.

        ## Detection

        What a reviewer looks at, and what does not count as a violation.

        ## Good example

        ```text
        code that follows the rule
        ```

        ## Bad example

        ```text
        code that violates the rule
        ```

        ## Change history

        - 1.0.0 ({date}): Initial rule.

        """;

    public static bool TryParseSeverity(string? value, out FindingSeverity severity)
    {
        severity = FindingSeverity.Medium;
        return value is "critical" or "high" or "medium" or "low" or "info" &&
               Enum.TryParse(value, ignoreCase: true, out severity);
    }

    public static string SeverityName(FindingSeverity severity) => severity.ToString().ToLowerInvariant();

    private static Dictionary<string, object> ParseFrontmatter(string text, ICollection<string> errors)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                errors.Add($"invalid frontmatter line '{Truncate(line)}'.");
                continue;
            }
            var key = line[..separator].Trim();
            var raw = line[(separator + 1)..].Trim();
            if (fields.ContainsKey(key))
            {
                errors.Add($"repeats frontmatter key '{key}'.");
                continue;
            }
            fields[key] = raw.StartsWith('[') && raw.EndsWith(']')
                ? raw[1..^1].Split(',').Select(value => Unquote(value.Trim())).Where(value => value.Length > 0).ToArray()
                : raw is "true" or "false"
                    ? raw == "true"
                    : Unquote(raw);
        }
        return fields;
    }

    private static Dictionary<string, string> ParseSections(string body, ICollection<string> errors)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var lines = new List<string>();
        void Flush()
        {
            if (current is not null) sections[current] = string.Join('\n', lines).Trim();
            lines.Clear();
        }
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                current = line[3..].Trim();
                continue;
            }
            if (current is not null) lines.Add(line);
        }
        Flush();
        foreach (var section in RequiredSections.Where(section =>
                     !sections.TryGetValue(section, out var content) || content.Length == 0))
            errors.Add($"requires a non-empty '## {section}' section.");
        return sections;
    }

    private static string ParseExample(Dictionary<string, string> sections, string heading, ICollection<string> errors)
    {
        var match = FenceRegex().Match(Section(sections, heading));
        if (!match.Success)
        {
            if (sections.ContainsKey(heading)) errors.Add($"'## {heading}' requires a fenced code block.");
            return string.Empty;
        }
        return match.Groups[1].Value.TrimEnd();
    }

    private static List<RuleChangeEntry> ParseChangeHistory(Dictionary<string, string> sections, ICollection<string> errors)
    {
        var entries = new List<RuleChangeEntry>();
        var section = Section(sections, "Change history");
        foreach (var item in HistorySplitRegex().Split(section))
        {
            var text = Collapse(item);
            if (text.Length == 0) continue;
            var match = HistoryItemRegex().Match(text);
            if (!match.Success ||
                !DateOnly.TryParseExact(match.Groups[2].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                errors.Add($"change-history item '{Truncate(text)}' must read '- <version> (<yyyy-mm-dd>): <change>'.");
                continue;
            }
            entries.Add(new RuleChangeEntry(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));
        }
        if (entries.Count == 0 && sections.ContainsKey("Change history"))
            errors.Add("requires at least one change-history item.");
        return entries;
    }

    private static string RequireString(Dictionary<string, object> fields, string key, ICollection<string> errors)
    {
        if (fields.TryGetValue(key, out var value) && value is string { Length: > 0 } text) return text;
        errors.Add($"requires a non-empty '{key}'.");
        return string.Empty;
    }

    private static bool RequireBoolean(Dictionary<string, object> fields, string key, ICollection<string> errors)
    {
        if (fields.TryGetValue(key, out var value) && value is bool flag) return flag;
        errors.Add($"requires '{key}' to be true or false.");
        return false;
    }

    private static IReadOnlyList<string> RequireList(Dictionary<string, object> fields, string key, ICollection<string> errors)
    {
        if (fields.TryGetValue(key, out var value) && value is string[] { Length: > 0 } list) return list;
        errors.Add($"requires a non-empty '{key}' list.");
        return [];
    }

    private static string Section(Dictionary<string, string> sections, string name) =>
        sections.TryGetValue(name, out var content) ? content : string.Empty;

    private static string Unquote(string value) =>
        value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0] ? value[1..^1] : value;

    private static string Collapse(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    private static string OneLine(string text) => Collapse(text);

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "…";

    [GeneratedRegex(CustomIdPattern)]
    private static partial Regex CustomIdRegex();

    [GeneratedRegex(@"```[a-z]*\n([\s\S]*?)```")]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"\n(?=- )")]
    private static partial Regex HistorySplitRegex();

    [GeneratedRegex(@"^- (\S+) \((\d{4}-\d{2}-\d{2})\): (.+)$")]
    private static partial Regex HistoryItemRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
