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
    IReadOnlyList<string> Kinds,
    string Statement,
    string Rationale,
    string Detection,
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
        var builtIn = BuiltIn.Value;
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
    /// Reads the hierarchy adapter out of a <c>qs-v1/&lt;adapter&gt;/&lt;level&gt;/&lt;hash&gt;</c> unit id.
    /// Returns null for an id that does not carry one, which resolves the whole library rather than
    /// failing a staleness scan on a malformed sidecar.
    /// </summary>
    public static string? AdapterFromUnitId(string? unitId)
    {
        var segments = unitId?.Split('/');
        return segments is { Length: 4 } && segments[0] == "qs-v1" &&
               segments[1] is "angular" or "dotnet" or "generic"
            ? segments[1]
            : null;
    }

    /// <summary>
    /// Decides whether a rule authored for <paramref name="technology"/> reaches a unit reviewed
    /// through <paramref name="adapter"/>. A rule matches its own technology, and
    /// <c>generic</c> rules match every adapter. A null adapter means "no technology filter" and is
    /// for inspection paths (the rules endpoint, <c>--explain-inputs</c>) that describe the whole
    /// library rather than one unit.
    /// </summary>
    public static bool AppliesTo(string technology, string? adapter) =>
        adapter is null ||
        string.Equals(technology, adapter, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(technology, "generic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Renders the effective, enabled rules as synthetic built-in review inputs so the existing
    /// <see cref="InputResolver"/>/prompt-budget machinery carries them into review prompts unchanged,
    /// each under its own stable heading id (e.g. <c>QS-NG-001</c>) for the model to cite as <c>ruleId</c>.
    /// Worked examples stay out of the prompt: they are for the rule's readers and the rules endpoint,
    /// and would spend the character budget several times over.
    /// </summary>
    public static IReadOnlyList<ReviewInput> RenderAsReviewInputs(
        ResolvedRuleCatalogue catalogue, string kind, string? adapter = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        return catalogue.Rules
            .Where(rule => rule.EffectiveEnabled &&
                           rule.Rule.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) &&
                           AppliesTo(rule.Rule.Technology, adapter))
            .Select(rule => new ReviewInput(
                rule.Rule.Id, "rule-library:" + rule.Rule.Id, "built-in", PriorityFor(rule.EffectiveSeverity),
                rule.Rule.Kinds, ["all"], true, Content(rule), string.Empty, false, rule.Rule.Version))
            .OrderByDescending(input => input.Priority)
            .ThenBy(input => input.Id, StringComparer.Ordinal)
            .ToArray();
    }

    // What the reviewer needs to apply the rule: what to do, and what to look at. The rationale and
    // the worked examples stay in the catalogue and on the rules endpoint, where a reader wants them
    // and no character budget is at stake.
    private static string Content(ResolvedRule rule)
    {
        var content = $"{rule.Rule.Title} ({rule.Rule.Technology}, {Severity(rule.EffectiveSeverity)} severity). " +
            $"{rule.Rule.Statement} Detection: {rule.Rule.Detection}";
        return rule.SeverityOverridden
            ? $"{content} Project override: report findings for this rule as " +
              $"{Severity(rule.EffectiveSeverity)} severity — {rule.OverrideReason}"
            : content;
    }

    private static string Severity(FindingSeverity severity) => severity.ToString().ToLowerInvariant();

    private static int PriorityFor(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 100,
        FindingSeverity.High => 85,
        FindingSeverity.Medium => 70,
        FindingSeverity.Low => 55,
        _ => 40,
    };

    // The embedded catalogue is immutable for the life of the process, and Resolve runs once per
    // reviewed unit on the tree, dashboard, and staleness paths. Parse it once.
    private static readonly Lazy<RuleCatalogueDocument> BuiltIn = new(ReadBuiltIn, isThreadSafe: true);

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

/// <summary>One catalogue rule an analyzer rule id enforces, as the effective catalogue resolves it.</summary>
public sealed record CatalogueRuleLink(
    string Id,
    string Title,
    string Technology,
    bool Enabled,
    FindingSeverity Severity);

/// <summary>
/// Maps analyzer-native rule ids (Roslyn <c>CA2016</c>, compiler <c>CS4014</c>, ESLint
/// <c>@angular-eslint/template/no-call-expression</c>, TypeScript <c>TS2322</c>, a sensor's own
/// <c>architecture/missing-directory</c>) to the named catalogue rules whose <c>deterministicRuleIds</c> list
/// them. That makes one rule enforceable twice: an analyzer reports the violation deterministically,
/// and the review agent, handed the same finding with the rule id attached, explains it in the rule's
/// terms instead of reporting it again under an invented id.
/// <para>
/// Ids match case-insensitively. An entry ending in <c>*</c> is a prefix, for a rule that owns a
/// whole diagnostic family; an exact entry always wins over a prefix. Retired rules
/// (<c>enabled: false</c> in the catalogue) are absent from the resolved catalogue and never match;
/// rules a project override disabled still match, flagged <see cref="CatalogueRuleLink.Enabled"/>
/// false, so the analyzer result stays explainable while the project has opted out of the rule.
/// </para>
/// </summary>
public sealed class DeterministicRuleMap
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CatalogueRuleLink>> exact;
    private readonly IReadOnlyList<(string Prefix, CatalogueRuleLink Link)> prefixes;

    private DeterministicRuleMap(
        IReadOnlyDictionary<string, IReadOnlyList<CatalogueRuleLink>> exact,
        IReadOnlyList<(string Prefix, CatalogueRuleLink Link)> prefixes)
    {
        this.exact = exact;
        this.prefixes = prefixes;
    }

    public static DeterministicRuleMap Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<CatalogueRuleLink>>(StringComparer.OrdinalIgnoreCase), []);

    public static DeterministicRuleMap From(ResolvedRuleCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        var exact = new Dictionary<string, List<CatalogueRuleLink>>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<(string Prefix, CatalogueRuleLink Link)>();
        foreach (var rule in catalogue.Rules)
        {
            var link = new CatalogueRuleLink(
                rule.Rule.Id, rule.Rule.Title, rule.Rule.Technology, rule.EffectiveEnabled, rule.EffectiveSeverity);
            foreach (var analyzerId in rule.Rule.DeterministicRuleIds ?? [])
            {
                if (string.IsNullOrWhiteSpace(analyzerId)) continue;
                var id = analyzerId.Trim();
                if (id.EndsWith('*'))
                {
                    if (id.Length > 1) prefixes.Add((id[..^1], link));
                    continue;
                }
                if (!exact.TryGetValue(id, out var links)) exact[id] = links = [];
                if (!links.Any(existing => existing.Id == link.Id)) links.Add(link);
            }
        }
        return new DeterministicRuleMap(
            exact.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<CatalogueRuleLink>)pair.Value.OrderBy(link => link.Id, StringComparer.Ordinal).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            prefixes.OrderByDescending(entry => entry.Prefix.Length)
                .ThenBy(entry => entry.Link.Id, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>The catalogue rules <paramref name="analyzerRuleId"/> enforces; empty when none claims it.</summary>
    public IReadOnlyList<CatalogueRuleLink> For(string? analyzerRuleId)
    {
        if (string.IsNullOrWhiteSpace(analyzerRuleId)) return [];
        if (exact.TryGetValue(analyzerRuleId, out var links)) return links;
        var longest = prefixes
            .Where(entry => analyzerRuleId.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (longest.Length == 0) return [];
        var length = longest[0].Prefix.Length;
        return longest.Where(entry => entry.Prefix.Length == length).Select(entry => entry.Link).ToArray();
    }

    /// <summary>
    /// The links of every distinct rule id in <paramref name="ruleIds"/> that maps to at least one
    /// catalogue rule, keyed by the analyzer rule id as reported.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CatalogueRuleLink>> ForAll(IEnumerable<string?> ruleIds) =>
        ruleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .Select(id => KeyValuePair.Create(id, For(id)))
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
