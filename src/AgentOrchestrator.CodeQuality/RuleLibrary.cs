using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleExample(string Language, string Code, string? Caption = null, string? Source = null);

public sealed record RuleChangelogEntry(string Version, string Date, string Summary);

/// <summary>
/// A single named rule from the rules/&lt;technology&gt;/&lt;id&gt;.json tree, as documented in
/// docs/concepts/rule-library.md and validated against schemas/rule.v1.schema.json.
/// </summary>
public sealed record RuleDefinition(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
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
    IReadOnlyList<string>? Tags,
    string Version,
    IReadOnlyList<RuleChangelogEntry>? Changelog,
    string Status = "active",
    string? SupersededBy = null);

/// <summary>
/// Loads the embedded rules/**/*.json tree (linked into this assembly the same way prompts/ and
/// catalogues/ are) and exposes it both as typed rules and as catalogue entries that plug into the
/// existing GuidelineStore install/citation mechanism, so a rule's stable id becomes the guideline
/// heading a review prompt cites back in a finding's ruleId.
/// </summary>
public static class RuleLibrary
{
    private const string ResourceMarker = ".rules.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly HashSet<string> Severities = ["critical", "high", "medium", "low", "info"];

    public static IReadOnlyList<RuleDefinition> Rules { get; } = Load();

    public static IReadOnlyList<GuidelineCatalogueEntry> CatalogueEntries { get; } =
        Rules.Select(ToCatalogueEntry).ToArray();

    public static RuleDefinition Get(string id) =>
        Rules.SingleOrDefault(rule => string.Equals(rule.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Rule '{id}' was not found in the rule library.");

    public static bool IsSupportedSeverity(string severity) => Severities.Contains(severity);

    public static GuidelineDraft CreateGuidelineDraft(
        RuleDefinition rule,
        string? severityOverride = null,
        string? overrideReason = null,
        bool includeExamples = true)
    {
        var severity = severityOverride ?? rule.Severity;
        if (!Severities.Contains(severity))
            throw new ArgumentException($"Unsupported rule severity '{severity}'.", nameof(severityOverride));
        return new GuidelineDraft(
            rule.Id,
            true,
            Priority(severity),
            rule.Kinds,
            rule.Levels,
            RenderContent(rule, severity, overrideReason, includeExamples));
    }

    public static string RenderContent(
        RuleDefinition rule,
        string? effectiveSeverity = null,
        string? overrideReason = null,
        bool includeExamples = true)
    {
        var severity = effectiveSeverity ?? rule.Severity;
        var builder = new StringBuilder();
        builder.Append('[').Append(rule.Id).Append("] ").Append(rule.Title.Trim());
        builder.Append("\n\n").Append(rule.Statement.Trim());
        builder.Append("\n\nWhy this rule exists: ").Append(rule.Rationale.Trim());
        builder.Append("\n\nSeverity: ").Append(severity)
            .Append(". Autofixable: ").Append(rule.Autofixable ? "yes" : "no").Append('.');
        if (!string.Equals(severity, rule.Severity, StringComparison.Ordinal))
        {
            builder.Append(" Project override: library default is ").Append(rule.Severity).Append('.');
            if (!string.IsNullOrWhiteSpace(overrideReason))
                builder.Append(" Reason: ").Append(overrideReason.Trim());
        }
        if (includeExamples)
        {
            AppendExample(builder, "Good", rule.Good);
            AppendExample(builder, "Avoid", rule.Bad);
        }
        return builder.ToString();
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

    public static int Priority(string severity) => severity switch
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
        if (rule.Schema != "https://quality.studio/schemas/rule.v1.schema.json" || rule.SchemaVersion != 1)
            throw new JsonException($"Rule resource '{source}' has an unsupported schema.");
        if (string.IsNullOrWhiteSpace(rule.Id) ||
            !Regex.IsMatch(rule.Id, "^QS-[A-Z]{2,6}-[0-9]{3}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(rule.Technology) ||
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
        if (rule.Changelog is not { Count: > 0 } ||
            !string.Equals(rule.Changelog[0].Version, rule.Version, StringComparison.Ordinal))
            throw new JsonException($"Rule resource '{source}' requires a changelog headed by its current version.");
        if (rule.Status is not ("active" or "deprecated") ||
            rule.Status == "deprecated" && string.IsNullOrWhiteSpace(rule.SupersededBy))
            throw new JsonException($"Rule resource '{source}' has invalid deprecation metadata.");
        var expectedSuffix = $".rules.{rule.Technology}.{rule.Id}.json";
        if (!source.EndsWith(expectedSuffix, StringComparison.Ordinal))
            throw new JsonException(
                $"Rule resource '{source}' directory and file name must match technology '{rule.Technology}' and id '{rule.Id}'.");
    }
}
