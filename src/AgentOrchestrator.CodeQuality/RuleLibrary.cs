using System.Reflection;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleDeterministicCheck(string Tool, string RuleId);

public sealed record RuleDefinition(
    string Id,
    string Title,
    string Summary,
    string Technology,
    string Category,
    string Severity,
    bool Autofixable,
    bool DefaultOn,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Levels,
    string Version,
    string Status,
    string Content,
    RuleDeterministicCheck? DeterministicCheck);

/// <summary>
/// Loads Quality Studio's own named coding-rule library (<c>rules/**/*.md</c>, embedded at
/// build time so the built-in rules travel with the package regardless of which repository
/// is being reviewed) and resolves each repository's effective default-on set against its
/// optional <c>.quality/rules.json</c> overrides.
/// </summary>
public sealed partial class RuleLibrary
{
    private static readonly HashSet<string> Severities = ["critical", "high", "medium", "low", "info"];
    private static readonly HashSet<string> Technologies = ["angular", "dotnet"];
    private static readonly HashSet<string> Statuses = ["active", "deprecated"];
    private static readonly HashSet<string> ReviewKinds = ["code", "security", "performance"];
    private static readonly HashSet<string> ReviewLevels = Enum.GetNames<ReviewLevel>()
        .Select(value => value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public RuleLibrary(IReadOnlyList<RuleDefinition> rules)
    {
        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate rule id '{duplicate.Key}' in the rule library.");
        Rules = rules;
    }

    public static RuleLibrary Default { get; } = LoadEmbedded();

    public IReadOnlyList<RuleDefinition> Rules { get; }

    public RuleDefinition? Find(string id) =>
        Rules.SingleOrDefault(rule => string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies <c>.quality/rules.json</c> from <paramref name="repositoryRoot"/> and returns the effective enabled rules.</summary>
    public IReadOnlyList<RuleDefinition> ResolveDefaultOn(string repositoryRoot) =>
        ApplyOverrides(new RuleOverrideConfigurationStore(repositoryRoot).Read());

    public IReadOnlyList<RuleDefinition> ApplyOverrides(IReadOnlyList<RuleOverride> overrides)
    {
        var byId = Rules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);
        var enabled = Rules.ToDictionary(rule => rule.Id, rule => rule.DefaultOn, StringComparer.OrdinalIgnoreCase);
        foreach (var over in overrides)
        {
            if (!byId.ContainsKey(over.RuleId))
                throw new ArgumentException($"Rule override references unknown rule id '{over.RuleId}'.");
            enabled[over.RuleId] = over.Enabled;
        }
        return Rules.Where(rule => enabled[rule.Id]).ToArray();
    }

    private static RuleLibrary LoadEmbedded()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        var rules = assembly.GetManifestResourceNames()
            .Where(name => (name.Contains(".rules.angular.", StringComparison.Ordinal) ||
                             name.Contains(".rules.dotnet.", StringComparison.Ordinal)) &&
                            name.EndsWith(".md", StringComparison.Ordinal))
            .Select(name => Parse(name, ReadResource(assembly, name)))
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        if (rules.Length == 0) throw new InvalidDataException("No rules were found embedded in the assembly.");
        return new RuleLibrary(rules);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Embedded rule resource '{name}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static RuleDefinition Parse(string resourceName, string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidDataException($"Rule '{resourceName}' must start with YAML frontmatter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"Rule '{resourceName}' has unterminated frontmatter.");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException($"Rule '{resourceName}' has an invalid frontmatter line '{line}'.");
            fields[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        string Require(string key) => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Rule '{resourceName}' requires '{key}'.");

        var id = Require("id");
        if (!IdPattern().IsMatch(id)) throw new InvalidDataException($"Rule '{resourceName}' has an invalid id '{id}'.");
        var severity = Require("severity").ToLowerInvariant();
        if (!Severities.Contains(severity)) throw new InvalidDataException($"Rule '{resourceName}' has an unsupported severity '{severity}'.");
        var technology = Require("technology").ToLowerInvariant();
        if (!Technologies.Contains(technology)) throw new InvalidDataException($"Rule '{resourceName}' has an unsupported technology '{technology}'.");
        var status = Require("status").ToLowerInvariant();
        if (!Statuses.Contains(status)) throw new InvalidDataException($"Rule '{resourceName}' has an unsupported status '{status}'.");
        var version = Require("version");
        if (!VersionPattern().IsMatch(version)) throw new InvalidDataException($"Rule '{resourceName}' has an invalid version '{version}'.");
        var kinds = Values(Require("kinds"));
        if (kinds.Count == 0 || kinds.Any(kind => !ReviewKinds.Contains(kind)))
            throw new InvalidDataException($"Rule '{resourceName}' has unsupported kinds.");
        var levels = Values(Require("levels"));
        if (levels.Count == 0 || levels.Any(level => !ReviewLevels.Contains(level)))
            throw new InvalidDataException($"Rule '{resourceName}' has unsupported levels.");
        var autofixable = ParseBool(Require("autofixable"), resourceName, "autofixable");
        var defaultOn = ParseBool(Require("defaultOn"), resourceName, "defaultOn");
        var deterministicCheck = fields.TryGetValue("deterministicCheck", out var rawCheck)
            ? ParseDeterministicCheck(rawCheck, resourceName)
            : null;
        var content = text[(end + 5)..].Trim();
        if (content.Length == 0) throw new InvalidDataException($"Rule '{resourceName}' has empty content.");

        return new RuleDefinition(id, Require("title"), Require("summary"), technology, Require("category"),
            severity, autofixable, defaultOn, kinds, levels, version, status, content, deterministicCheck);
    }

    private static bool ParseBool(string raw, string resourceName, string field) =>
        bool.TryParse(raw, out var value) ? value : throw new InvalidDataException($"Rule '{resourceName}' requires '{field}' to be true or false.");

    private static IReadOnlyList<string> Values(string raw) =>
        raw.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('"', '\'').ToLowerInvariant())
            .ToArray();

    private static RuleDeterministicCheck ParseDeterministicCheck(string raw, string resourceName)
    {
        raw = raw.Trim();
        if (!raw.StartsWith('{') || !raw.EndsWith('}'))
            throw new InvalidDataException($"Rule '{resourceName}' has an invalid deterministicCheck value.");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf(':');
            if (separator <= 0) throw new InvalidDataException($"Rule '{resourceName}' has an invalid deterministicCheck entry '{part}'.");
            fields[part[..separator].Trim()] = part[(separator + 1)..].Trim().Trim('"', '\'');
        }
        if (!fields.TryGetValue("tool", out var tool) || !fields.TryGetValue("ruleId", out var ruleId) ||
            string.IsNullOrWhiteSpace(tool) || string.IsNullOrWhiteSpace(ruleId))
            throw new InvalidDataException($"Rule '{resourceName}' deterministicCheck requires 'tool' and 'ruleId'.");
        return new RuleDeterministicCheck(tool, ruleId);
    }

    [GeneratedRegex("^QS-[A-Z]{2,4}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
