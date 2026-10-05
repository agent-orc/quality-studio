using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One condition a rule must meet to be selected by a pack. Every field that is set must match
/// (and), a list matches when the rule carries any of its values (or). Selecting by technology,
/// kind or category instead of by id is what lets a pack pick up a rule a later library release
/// adds, without editing the pack.
/// </summary>
public sealed record RulePackSelector(
    IReadOnlyList<string>? Ids = null,
    IReadOnlyList<string>? Technologies = null,
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? Categories = null,
    bool? DefaultOn = null)
{
    public bool Selects(RuleDefinition rule) =>
        (Ids is null || Ids.Contains(rule.Id, StringComparer.Ordinal)) &&
        (Technologies is null || Technologies.Contains(rule.Technology, StringComparer.Ordinal)) &&
        (Kinds is null || rule.Kinds.Any(kind => Kinds.Contains(kind, StringComparer.Ordinal))) &&
        (Categories is null || Categories.Contains(rule.Category, StringComparer.Ordinal)) &&
        (DefaultOn is null || rule.DefaultOn == DefaultOn.Value);
}

/// <summary>
/// A named selection of rules for one kind of project (<c>rule-pack.v1</c>). A pack only decides
/// which rules apply; a severity or a single exception stays an override with its own reason.
/// </summary>
public sealed record RulePackDocument(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    string Id,
    string Version,
    string Title,
    string Description,
    IReadOnlyList<string> ProjectTypes,
    IReadOnlyList<RulePackSelector> Include)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/rule-pack.v1.schema.json";

    public bool Selects(RuleDefinition rule) => Include.Any(selector => selector.Selects(rule));
}

/// <summary>The generated built-in pack list, embedded next to the rule catalogue.</summary>
public sealed record RulePackCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    IReadOnlyList<RulePackDocument> Packs);

/// <summary>
/// Which packs decide a project's rule set (<c>rule-applicability.v1</c>). Present, it replaces the
/// house-style default for its scope: a rule applies when one of the packs selects it, and
/// overrides then adjust single rules.
/// </summary>
public sealed record RuleApplicabilityDocument(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    IReadOnlyList<string> Packs,
    string Reason)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/rule-applicability.v1.schema.json";
}

/// <summary>The packs in effect for one repository and the scope that chose them.</summary>
public sealed record RuleApplicability(string Scope, IReadOnlyList<string> Packs, string? Reason);

/// <summary>A pack as one repository sees it: where it is defined and which rules it selects there.</summary>
public sealed record ResolvedRulePack(RulePackDocument Pack, string Origin, IReadOnlyList<string> RuleIds);

public static partial class RulePackRules
{
    /// <summary>The pack that reproduces the shipped defaults: every rule marked <c>defaultOn</c>.</summary>
    public const string HouseStyleId = "house-style";

    public const int MaxPacksPerScope = 50;
    public const int MaxSelectorsPerPack = 50;

    private static readonly string[] Technologies = ["angular", "dotnet", "generic"];
    private static readonly string[] Kinds = ["code", "security", "performance"];

    /// <summary>
    /// Validates a pack's own shape. Whether its <c>ids</c> exist is checked by the resolver, which
    /// knows the repository's custom rules.
    /// </summary>
    public static void Validate(RulePackDocument? pack, string? expectedId, ICollection<string> errors)
    {
        if (pack is null)
        {
            errors.Add("is empty.");
            return;
        }
        if (pack.SchemaVersion != 1) errors.Add("must declare schemaVersion 1.");
        if (string.IsNullOrWhiteSpace(pack.Id) || !PackIdRegex().IsMatch(pack.Id))
            errors.Add($"id '{pack.Id}' must be lower-case letters, digits and hyphens (2 to 64 characters).");
        else if (expectedId is not null && !string.Equals(pack.Id, expectedId, StringComparison.Ordinal))
            errors.Add($"id '{pack.Id}' must match its file name or route '{expectedId}'.");
        if (string.IsNullOrWhiteSpace(pack.Version)) errors.Add("requires a version.");
        if (string.IsNullOrWhiteSpace(pack.Title) || pack.Title.Length > 120) errors.Add("requires a title of at most 120 characters.");
        if (pack.Description is null || pack.Description.Length > 2000) errors.Add("requires a description of at most 2000 characters.");
        if (pack.ProjectTypes is null) errors.Add("requires a projectTypes list (it may be empty).");
        else if (pack.ProjectTypes.Any(value => value is null)) errors.Add("projectTypes must not contain null.");
        if (pack.Include is null || pack.Include.Count == 0)
        {
            errors.Add("requires at least one include selector.");
            return;
        }
        if (pack.Include.Count > MaxSelectorsPerPack) errors.Add($"has more than {MaxSelectorsPerPack} selectors.");
        foreach (var (selector, index) in pack.Include.Select((selector, index) => (selector, index)))
        {
            var label = $"include[{index}]";
            if (selector is null)
            {
                errors.Add($"{label} is empty.");
                continue;
            }
            if (selector.Ids is null && selector.Technologies is null && selector.Kinds is null &&
                selector.Categories is null && selector.DefaultOn is null)
                errors.Add($"{label} must set at least one of ids, technologies, kinds, categories, defaultOn.");
            foreach (var (name, values) in new[]
                     {
                         ("ids", selector.Ids), ("technologies", selector.Technologies),
                         ("kinds", selector.Kinds), ("categories", selector.Categories),
                     })
            {
                if (values is { Count: 0 }) errors.Add($"{label}.{name} must not be an empty list.");
                // A null id would otherwise reach the resolver's dictionary lookups and throw.
                else if (values?.Any(value => value is null) == true) errors.Add($"{label}.{name} must not contain null.");
            }
            foreach (var value in (selector.Technologies ?? []).Where(value => value is not null && !Technologies.Contains(value, StringComparer.Ordinal)))
                errors.Add($"{label}.technologies value '{value}' must be one of angular, dotnet, generic.");
            foreach (var value in (selector.Kinds ?? []).Where(value => value is not null && !Kinds.Contains(value, StringComparer.Ordinal)))
                errors.Add($"{label}.kinds value '{value}' must be one of code, security, performance.");
        }
    }

    public static bool IsValidPackId(string? id) => id is not null && PackIdRegex().IsMatch(id);

    internal static readonly JsonSerializerOptions StrictJson = CreateStrictJson();

    private static JsonSerializerOptions CreateStrictJson()
    {
        var options = new JsonSerializerOptions(AttackCoverageJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,63}$")]
    private static partial Regex PackIdRegex();
}
