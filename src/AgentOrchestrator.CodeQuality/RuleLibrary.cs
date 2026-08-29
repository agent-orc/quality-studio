using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleChangeEntry(string Version, string Date, string Change);

public sealed record RuleDefinition(
    string Id,
    string Version,
    string Title,
    string Technology,
    string Category,
    string Statement,
    string Rationale,
    string GoodExample,
    string BadExample,
    FindingSeverity Severity,
    bool DefaultOn,
    bool Autofixable,
    IReadOnlyList<string> DeterministicRuleIds,
    string? RelatedGuideline,
    string Since,
    IReadOnlyList<RuleChangeEntry> ChangeHistory,
    bool Enabled = true);

public sealed record RuleCatalogueDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string CatalogueVersion,
    IReadOnlyList<RuleDefinition> Entries);

public sealed record RuleOverride(string Id, bool? Enabled, FindingSeverity? Severity, string Reason);

public sealed record RuleOverrideDocument(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    IReadOnlyList<RuleOverride> Overrides);

public sealed record ResolvedRule(
    RuleDefinition Rule,
    bool EffectiveEnabled,
    FindingSeverity EffectiveSeverity,
    bool SeverityOverridden,
    string Scope,
    string? OverrideReason);

public sealed record ResolvedRuleCatalogue(
    string CatalogueVersion,
    IReadOnlyList<ResolvedRule> Rules,
    IReadOnlyList<string> Sources);

/// <summary>
/// Resolves the named-rule library the same way <see cref="AttackCatalogueResolver"/> resolves the
/// attack catalogue: a built-in embedded seed set, layered with an optional shared "global" override
/// file, then an optional per-repository "project" override file (project wins by rule id).
/// </summary>
public sealed class RuleCatalogueResolver
{
    public const string ProjectRelativePath = ".quality/rules/overrides.json";
    public const string GlobalFileName = "rule-overrides.json";
    private const string BuiltInResourceSuffix = "catalogues.rule-catalogue.v1.json";

    public ResolvedRuleCatalogue Resolve(string repositoryRoot, string? globalInputsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var builtIn = ReadBuiltIn();
        var sources = new List<string> { "embedded:" + BuiltInResourceSuffix };
        var overridesById = new Dictionary<string, (RuleOverride Override, string Source)>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(globalInputsDirectory))
        {
            var globalPath = Path.Combine(Path.GetFullPath(globalInputsDirectory), GlobalFileName);
            if (File.Exists(globalPath)) Apply(ReadOverrides(globalPath), globalPath, builtIn, overridesById, sources);
        }
        var projectPath = Path.Combine(Path.GetFullPath(repositoryRoot),
            ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(projectPath)) Apply(ReadOverrides(projectPath), projectPath, builtIn, overridesById, sources);

        var resolved = builtIn.Entries
            .Where(rule => rule.Enabled)
            .Select(rule =>
            {
                var hasOverride = overridesById.TryGetValue(rule.Id, out var found);
                var effectiveEnabled = hasOverride && found.Override.Enabled.HasValue
                    ? found.Override.Enabled.Value : rule.DefaultOn;
                var severityOverridden = hasOverride && found.Override.Severity.HasValue;
                var effectiveSeverity = severityOverridden ? found.Override.Severity!.Value : rule.Severity;
                return new ResolvedRule(rule, effectiveEnabled, effectiveSeverity, severityOverridden,
                    hasOverride ? found.Source : "built-in", hasOverride ? found.Override.Reason : null);
            })
            .OrderBy(rule => rule.Rule.Id, StringComparer.Ordinal)
            .ToArray();

        return new ResolvedRuleCatalogue(builtIn.CatalogueVersion, resolved, sources);
    }

    /// <summary>
    /// Renders the effective, enabled rules as synthetic "global" review inputs so the existing
    /// <see cref="InputResolver"/>/prompt-budget machinery carries them into review prompts unchanged,
    /// each under its own stable heading id (e.g. <c>QS-NG-001</c>) for the model to cite as <c>ruleId</c>.
    /// </summary>
    public static IReadOnlyList<ReviewInput> RenderAsReviewInputs(ResolvedRuleCatalogue catalogue, string kind)
    {
        if (!string.Equals(kind, "code", StringComparison.OrdinalIgnoreCase)) return [];
        return catalogue.Rules
            .Where(rule => rule.EffectiveEnabled)
            .Select(rule => new ReviewInput(
                rule.Rule.Id, "rule-library:" + rule.Rule.Id, "global", PriorityFor(rule.EffectiveSeverity),
                ["code"], ["all"], true, Content(rule), string.Empty, false))
            .OrderByDescending(input => input.Priority)
            .ThenBy(input => input.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Content(ResolvedRule rule) => rule.SeverityOverridden
        ? $"{rule.Rule.Statement} {rule.Rule.Rationale} (Project override: findings for this rule are treated as " +
          $"{rule.EffectiveSeverity.ToString().ToLowerInvariant()} severity — {rule.OverrideReason})"
        : $"{rule.Rule.Statement} {rule.Rule.Rationale}";

    private static int PriorityFor(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 100,
        FindingSeverity.High => 85,
        FindingSeverity.Medium => 70,
        FindingSeverity.Low => 55,
        _ => 40,
    };

    private static RuleCatalogueDocument ReadBuiltIn()
    {
        var assembly = typeof(RuleCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in rule catalogue is unavailable.");
        var document = JsonSerializer.Deserialize<RuleCatalogueDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException("The built-in rule catalogue is empty.");
        Validate(document, "built-in");
        return document;
    }

    private static RuleOverrideDocument ReadOverrides(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RuleOverrideDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException($"Rule override file '{path}' is empty.");
    }

    private static void Apply(RuleOverrideDocument document, string source, RuleCatalogueDocument builtIn,
        Dictionary<string, (RuleOverride Override, string Source)> overridesById, List<string> sources)
    {
        if (document.SchemaVersion != 1 || document.Overrides is null)
            throw new JsonException($"Rule override file '{source}' has an unsupported contract.");
        var knownIds = builtIn.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in document.Overrides)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !knownIds.Contains(entry.Id))
                throw new JsonException($"Rule override file '{source}' references unknown rule id '{entry.Id}'.");
            if (entry.Enabled is null && entry.Severity is null)
                throw new JsonException($"Rule override file '{source}' entry '{entry.Id}' must set enabled or severity.");
            if (string.IsNullOrWhiteSpace(entry.Reason))
                throw new JsonException($"Rule override file '{source}' entry '{entry.Id}' requires a reason.");
            overridesById[entry.Id] = (entry, source);
        }
        sources.Add(source);
    }

    private static void Validate(RuleCatalogueDocument document, string source)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.CatalogueVersion) || document.Entries is null)
            throw new JsonException($"Rule catalogue '{source}' has an unsupported contract.");
        if (document.Entries.GroupBy(entry => entry.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException($"Rule catalogue '{source}' contains duplicate ids.");
        if (document.Entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version) ||
                string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Statement) ||
                string.IsNullOrWhiteSpace(entry.Rationale) || string.IsNullOrWhiteSpace(entry.Since)))
            throw new JsonException($"Rule catalogue '{source}' contains an invalid entry.");
    }
}
