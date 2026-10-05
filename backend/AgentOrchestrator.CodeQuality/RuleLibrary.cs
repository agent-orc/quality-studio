using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    // Written as null rather than omitted: rule-catalogue.v1 requires the property on every entry,
    // including a custom rule travelling in an exported rule set.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RelatedGuideline,
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
    IReadOnlyList<RuleOverride> Overrides)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json";
}

/// <summary>
/// The resolved state of one rule for one repository.
/// <para><paramref name="Scope"/> is the override file that decided the rule, or <c>built-in</c>.</para>
/// <para><paramref name="Origin"/> is where the rule is defined: <c>built-in</c>, <c>global</c>, or <c>project</c>.</para>
/// <para><paramref name="SelectedBy"/> lists the applicable packs that select the rule.</para>
/// <para><paramref name="OverrideScope"/> is <c>global</c> or <c>project</c> when an override decided it.</para>
/// </summary>
public sealed record ResolvedRule(
    RuleDefinition Rule,
    bool EffectiveEnabled,
    FindingSeverity EffectiveSeverity,
    bool SeverityOverridden,
    string Scope,
    string? OverrideReason,
    string Origin = RuleScopes.BuiltIn,
    IReadOnlyList<string>? SelectedBy = null,
    string? OverrideScope = null);

/// <summary>
/// A configuration problem, located by scope and a display path that never contains an absolute
/// directory, so it can be shown to an API caller as it is.
/// </summary>
public sealed record RuleDiagnostic(string Scope, string Source, string? Subject, string Message)
{
    public override string ToString() =>
        Subject is null ? $"{Source}: {Message}" : $"{Source} ({Subject}): {Message}";
}

/// <summary>A custom rule or pack file exactly as it is stored, for the management surface.</summary>
public sealed record RuleSourceFile(string? Id, string FileName, string Content);

/// <summary>What one writable scope currently contributes to the rule pool.</summary>
public sealed record RuleScopeState(
    string Scope,
    IReadOnlyList<RuleOverride> Overrides,
    RuleApplicabilityDocument? Applicability,
    IReadOnlyList<RuleSourceFile> CustomRules,
    IReadOnlyList<RuleSourceFile> Packs);

public sealed record ResolvedRuleCatalogue(
    string CatalogueVersion,
    IReadOnlyList<ResolvedRule> Rules,
    IReadOnlyList<string> Sources)
{
    /// <summary>The packs that decide which rules apply, and the scope that chose them.</summary>
    public RuleApplicability Applicability { get; init; } =
        new(RuleScopes.Default, [RulePackRules.HouseStyleId], null);

    /// <summary>Every pack this repository can select, with the rules it selects here.</summary>
    public IReadOnlyList<ResolvedRulePack> Packs { get; init; } = [];

    /// <summary>Configuration problems. Reviews refuse to run while any exist.</summary>
    public IReadOnlyList<RuleDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>The distinct scopes <see cref="Sources"/> came from, for callers that must not see paths.</summary>
    public IReadOnlyList<string> SourceScopes { get; init; } = [RuleScopes.BuiltIn];

    /// <summary>The global, shared and project contributions, keyed by scope.</summary>
    public IReadOnlyDictionary<string, RuleScopeState> ScopeStates { get; init; } =
        new Dictionary<string, RuleScopeState>(StringComparer.Ordinal);

    public bool IsValid => Diagnostics.Count == 0;
}

public static class RuleScopes
{
    public const string BuiltIn = "built-in";
    public const string Global = "global";
    public const string Project = "project";
    /// <summary>The read-only <c>rule-overrides.json</c> in a registration's global inputs directory.</summary>
    public const string SharedGlobal = "global-inputs";
    /// <summary>No applicability file anywhere: the house-style pack applies.</summary>
    public const string Default = "default";

    public static bool IsWritable(string? scope) => scope is Global or Project;
}

/// <summary>
/// Thrown when the rule pool cannot be resolved. It derives from <see cref="JsonException"/> so the
/// review, tree and API paths keep failing closed the way they did for a broken override file: a
/// rule the repository believes it disabled must never be silently active.
/// </summary>
public sealed class RuleConfigurationException(IReadOnlyList<RuleDiagnostic> diagnostics)
    : JsonException(Describe(diagnostics))
{
    public IReadOnlyList<RuleDiagnostic> Diagnostics { get; } = diagnostics;

    private static string Describe(IReadOnlyList<RuleDiagnostic> diagnostics) =>
        "The rule pool is invalid: " + string.Join(" ", diagnostics.Take(5).Select(value => value.ToString())) +
        (diagnostics.Count > 5 ? $" And {diagnostics.Count - 5} more." : string.Empty);
}

/// <summary>Where one repository's rule pool is read from.</summary>
public sealed record RulePoolLocations(
    string RepositoryRoot,
    string ProjectDirectory,
    string GlobalDirectory,
    string? SharedOverridesPath);

/// <summary>The raw contents of one scope's rule folder, read once so a change can be validated in memory.</summary>
public sealed record RuleScopeSources(
    string Scope,
    string Directory,
    string? Overrides,
    string? Applicability,
    IReadOnlyDictionary<string, string> CustomRules,
    IReadOnlyDictionary<string, string> Packs,
    IReadOnlyList<RuleDiagnostic> ReadErrors)
{
    public const string OverridesFileName = "overrides.json";
    public const string ApplicabilityFileName = "applicability.json";
    public const string CustomDirectoryName = "custom";
    public const string PacksDirectoryName = "packs";
    public const int MaxCustomRules = 200;
    public const int MaxConfigurationBytes = 256 * 1024;

    public static RuleScopeSources Empty(string scope, string directory) => new(
        scope, directory, null, null,
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal), []);

    /// <summary>A path a caller may see: relative to the scope folder, never absolute.</summary>
    public string Display(string relative) => Scope == RuleScopes.Project
        ? ".quality/rules/" + relative
        : "global rules/" + relative;

    public static RuleScopeSources Read(string scope, string directory)
    {
        var errors = new List<RuleDiagnostic>();
        var empty = Empty(scope, directory);
        if (IsDirectoryLink(directory))
        {
            errors.Add(new RuleDiagnostic(scope, empty.Display(""), null,
                "is a symbolic link; rule-pool configuration must be stored in a regular directory."));
            return empty with { ReadErrors = errors };
        }
        string? ReadConfiguration(string relative)
        {
            var path = Path.Combine(directory, relative);
            if (IsFileLink(path))
            {
                errors.Add(new RuleDiagnostic(scope, empty.Display(relative), null,
                    "is a symbolic link; rule-pool configuration must be stored in a regular file."));
                return null;
            }
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaxConfigurationBytes)
            {
                errors.Add(new RuleDiagnostic(scope, empty.Display(relative), null,
                    $"exceeds the {MaxConfigurationBytes / 1024} KiB configuration size limit."));
                return null;
            }
            return File.ReadAllText(path);
        }
        Dictionary<string, string> ReadFolder(string folder, string extension, int limit, int maxBytes)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(directory, folder);
            if (IsDirectoryLink(path))
            {
                errors.Add(new RuleDiagnostic(scope, empty.Display(folder + "/"), null,
                    "is a symbolic link; rule-pool configuration must be stored in a regular directory."));
                return files;
            }
            if (!System.IO.Directory.Exists(path)) return files;
            var candidates = System.IO.Directory.EnumerateFiles(path, "*" + extension, SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > limit)
                errors.Add(new RuleDiagnostic(scope, empty.Display(folder + "/"), null,
                    $"holds {candidates.Length} files; at most {limit} are read."));
            foreach (var file in candidates.Take(limit))
            {
                var name = Path.GetFileName(file);
                if (IsFileLink(file))
                {
                    errors.Add(new RuleDiagnostic(scope, empty.Display(folder + "/" + name), null,
                        "is a symbolic link; rule-pool configuration must be stored in a regular file."));
                    continue;
                }
                if (new FileInfo(file).Length > maxBytes)
                {
                    errors.Add(new RuleDiagnostic(scope, empty.Display(folder + "/" + name), null,
                        $"exceeds the {maxBytes / 1024} KiB size limit."));
                    continue;
                }
                files[name] = File.ReadAllText(file);
            }
            return files;
        }

        return new RuleScopeSources(
            scope,
            directory,
            ReadConfiguration(OverridesFileName),
            ReadConfiguration(ApplicabilityFileName),
            ReadFolder(CustomDirectoryName, ".md", MaxCustomRules, RuleMarkdown.MaxRuleBytes),
            ReadFolder(PacksDirectoryName, ".json", RulePackRules.MaxPacksPerScope, MaxConfigurationBytes),
            errors);
    }

    /// <summary>
    /// A cheap change detector over the same files <see cref="Read"/> reads: names, sizes and write
    /// times, without opening a file.
    /// </summary>
    internal static void AppendFingerprint(StringBuilder builder, string directory)
    {
        builder.Append(directory).Append('\n');
        AppendDirectoryLink(builder, directory);
        foreach (var name in new[] { OverridesFileName, ApplicabilityFileName })
            AppendFile(builder, Path.Combine(directory, name));
        foreach (var (folder, extension) in new[] { (CustomDirectoryName, ".md"), (PacksDirectoryName, ".json") })
        {
            var path = Path.Combine(directory, folder);
            AppendDirectoryLink(builder, path);
            if (!System.IO.Directory.Exists(path)) continue;
            foreach (var file in System.IO.Directory.EnumerateFiles(path, "*" + extension, SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
                AppendFile(builder, file);
        }
    }

    internal static void AppendFile(StringBuilder builder, string? path)
    {
        if (path is null) return;
        var info = new FileInfo(path);
        if (info.LinkTarget is { } target)
            builder.Append(path).Append("|link|").Append(target).Append('\n');
        if (!info.Exists) return;
        builder.Append(path).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendDirectoryLink(StringBuilder builder, string path)
    {
        if (new DirectoryInfo(path).LinkTarget is { } target)
            builder.Append(path).Append("|link|").Append(target).Append('\n');
    }

    private static bool IsFileLink(string path) => new FileInfo(path).LinkTarget is not null;

    private static bool IsDirectoryLink(string path) => new DirectoryInfo(path).LinkTarget is not null;
}

/// <summary>Everything one repository's rule pool is resolved from.</summary>
public sealed record RulePoolSources(
    RuleScopeSources Global,
    string? SharedOverrides,
    string? SharedOverridesPath,
    RuleScopeSources Project)
{
    public static RulePoolSources Read(RulePoolLocations locations)
    {
        string? shared = null;
        if (locations.SharedOverridesPath is not null && File.Exists(locations.SharedOverridesPath))
            shared = File.ReadAllText(locations.SharedOverridesPath);
        return new RulePoolSources(
            RuleScopeSources.Read(RuleScopes.Global, locations.GlobalDirectory),
            shared,
            locations.SharedOverridesPath,
            RuleScopeSources.Read(RuleScopes.Project, locations.ProjectDirectory));
    }

    public RuleScopeSources Scope(string scope) => scope switch
    {
        RuleScopes.Global => Global,
        RuleScopes.Project => Project,
        _ => throw new ArgumentException($"'{scope}' is not a writable rule scope."),
    };

    public RulePoolSources With(RuleScopeSources scope) => scope.Scope switch
    {
        RuleScopes.Global => this with { Global = scope },
        RuleScopes.Project => this with { Project = scope },
        _ => throw new ArgumentException($"'{scope.Scope}' is not a writable rule scope."),
    };
}

/// <summary>
/// Resolves the named-rule pool for one repository. The layers, lowest first:
/// <list type="number">
/// <item>the built-in library embedded in this assembly;</item>
/// <item>the host-wide <em>global</em> rule folder in the data root (<c>&lt;data root&gt;/rules/</c>);</item>
/// <item>the read-only <c>rule-overrides.json</c> in a registration's global inputs directory;</item>
/// <item>the repository's own <c>.quality/rules/</c> folder.</item>
/// </list>
/// Global and project folders may add custom rules (<c>custom/*.md</c>, the authored rule format),
/// custom packs (<c>packs/*.json</c>), an applicability file choosing the packs that replace the
/// house-style default, and overrides. A later layer wins by rule id for overrides; applicability is
/// decided by the most specific scope that declares one.
/// </summary>
public sealed class RuleCatalogueResolver(string? globalRulesDirectory = null)
{
    public const string ProjectRelativePath = ".quality/rules/overrides.json";
    public const string ProjectDirectoryRelativePath = ".quality/rules";
    public const string GlobalFileName = "rule-overrides.json";
    public const string GlobalDirectoryName = "rules";
    private const string BuiltInResourceSuffix = "catalogues.rule-catalogue.v1.json";
    private const string BuiltInPacksResourceSuffix = "catalogues.rule-packs.v1.json";
    private const int MaxCachedPools = 64;

    // Resolve runs once per reviewed unit on the tree, dashboard and staleness paths. The cache
    // keeps that at a handful of stat calls per unit; a changed file changes the fingerprint.
    private static readonly ConcurrentDictionary<string, (string Fingerprint, ResolvedRuleCatalogue Catalogue)> Cache =
        new(StringComparer.Ordinal);

    /// <summary>The host-wide rule folder: <c>&lt;data root base&gt;/rules</c> unless the host chose another.</summary>
    public string GlobalRulesDirectory =>
        globalRulesDirectory is null
            ? Path.Combine(QualityDataRoot.BaseDirectory, GlobalDirectoryName)
            : Path.GetFullPath(globalRulesDirectory);

    public RulePoolLocations Locate(string repositoryRoot, string? globalInputsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        return new RulePoolLocations(
            root,
            Path.Combine(root, ProjectDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            GlobalRulesDirectory,
            string.IsNullOrWhiteSpace(globalInputsDirectory)
                ? null
                : Path.Combine(Path.GetFullPath(globalInputsDirectory), GlobalFileName));
    }

    /// <summary>Resolves the pool, failing closed with <see cref="RuleConfigurationException"/> when it is invalid.</summary>
    public ResolvedRuleCatalogue Resolve(string repositoryRoot, string? globalInputsDirectory = null)
    {
        var catalogue = Inspect(repositoryRoot, globalInputsDirectory);
        return catalogue.IsValid ? catalogue : throw new RuleConfigurationException(catalogue.Diagnostics);
    }

    /// <summary>
    /// Resolves the pool without throwing on configuration problems: invalid parts are left out and
    /// reported in <see cref="ResolvedRuleCatalogue.Diagnostics"/>. For the management surface, which
    /// must be able to show and repair a broken configuration.
    /// </summary>
    public ResolvedRuleCatalogue Inspect(string repositoryRoot, string? globalInputsDirectory = null)
    {
        var locations = Locate(repositoryRoot, globalInputsDirectory);
        var key = locations.RepositoryRoot + "\0" + locations.GlobalDirectory + "\0" + locations.SharedOverridesPath;
        var fingerprint = Fingerprint(locations);
        if (Cache.TryGetValue(key, out var cached) && StringComparer.Ordinal.Equals(cached.Fingerprint, fingerprint))
            return cached.Catalogue;
        var catalogue = Build(RulePoolSources.Read(locations));
        if (Cache.Count >= MaxCachedPools) Cache.Clear();
        Cache[key] = (fingerprint, catalogue);
        return catalogue;
    }

    /// <summary>
    /// A digest of every file the pool is read from, for callers that key their own caches on the
    /// effective review policy (the hierarchy snapshot, the tree ETag).
    /// </summary>
    public string SourceState(string repositoryRoot, string? globalInputsDirectory = null) =>
        Fingerprint(Locate(repositoryRoot, globalInputsDirectory));

    /// <summary>Drops every cached resolution; called after a write through <see cref="RulePoolStore"/>.</summary>
    public static void Invalidate() => Cache.Clear();

    private static string Fingerprint(RulePoolLocations locations)
    {
        var builder = new StringBuilder();
        RuleScopeSources.AppendFingerprint(builder, locations.GlobalDirectory);
        RuleScopeSources.AppendFile(builder, locations.SharedOverridesPath);
        RuleScopeSources.AppendFingerprint(builder, locations.ProjectDirectory);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>The embedded built-in library, parsed once per process.</summary>
    public static RuleCatalogueDocument BuiltInCatalogue => BuiltIn.Value;

    /// <summary>The embedded built-in packs, parsed once per process.</summary>
    public static IReadOnlyList<RulePackDocument> BuiltInPacks => BuiltInPackList.Value;

    /// <summary>
    /// Resolves a pool from sources already in memory. Pure: the same sources give the same result,
    /// which is what lets <see cref="RulePoolStore"/> validate a change before it writes anything.
    /// </summary>
    public static ResolvedRuleCatalogue Build(RulePoolSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var diagnostics = new List<RuleDiagnostic>();
        var builtIn = BuiltIn.Value;
        var sourcePaths = new List<string> { "embedded:" + BuiltInResourceSuffix };
        var sourceScopes = new List<string> { RuleScopes.BuiltIn };
        void Used(string path, string scope)
        {
            sourcePaths.Add(path);
            if (!sourceScopes.Contains(scope)) sourceScopes.Add(scope);
        }

        var definitions = new Dictionary<string, (RuleDefinition Rule, string Origin)>(StringComparer.Ordinal);
        foreach (var entry in builtIn.Entries) definitions[entry.Id] = (entry, RuleScopes.BuiltIn);

        var customFiles = new Dictionary<string, List<RuleSourceFile>>(StringComparer.Ordinal);
        foreach (var scope in new[] { sources.Global, sources.Project })
        {
            diagnostics.AddRange(scope.ReadErrors);
            var files = customFiles[scope.Scope] = [];
            foreach (var (fileName, text) in scope.CustomRules.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var errors = new List<string>();
                var rule = RuleMarkdown.ParseCustom(text, fileName, errors);
                var display = scope.Display(RuleScopeSources.CustomDirectoryName + "/" + fileName);
                files.Add(new RuleSourceFile(rule?.Id ?? PeekId(text), fileName, text));
                if (rule is null)
                {
                    diagnostics.AddRange(errors.Select(error => new RuleDiagnostic(scope.Scope, display, PeekId(text), error)));
                    continue;
                }
                if (definitions.TryGetValue(rule.Id, out var existing))
                {
                    diagnostics.Add(new RuleDiagnostic(scope.Scope, display, rule.Id,
                        $"id '{rule.Id}' is already defined by the {existing.Origin} rule pool."));
                    continue;
                }
                definitions[rule.Id] = (rule, scope.Scope);
                Used(Path.Combine(scope.Directory, RuleScopeSources.CustomDirectoryName, fileName), scope.Scope);
            }
        }

        var packs = new Dictionary<string, (RulePackDocument Pack, string Origin)>(StringComparer.Ordinal);
        foreach (var pack in BuiltInPackList.Value) packs[pack.Id] = (pack, RuleScopes.BuiltIn);
        var packFiles = new Dictionary<string, List<RuleSourceFile>>(StringComparer.Ordinal);
        foreach (var scope in new[] { sources.Global, sources.Project })
        {
            var files = packFiles[scope.Scope] = [];
            foreach (var (fileName, text) in scope.Packs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var display = scope.Display(RuleScopeSources.PacksDirectoryName + "/" + fileName);
                var expectedId = Path.GetFileNameWithoutExtension(fileName);
                files.Add(new RuleSourceFile(expectedId, fileName, text));
                var errors = new List<string>();
                RulePackDocument? pack = null;
                try
                {
                    pack = JsonSerializer.Deserialize<RulePackDocument>(text, RulePackRules.StrictJson);
                }
                catch (JsonException exception)
                {
                    errors.Add("is not a valid rule-pack.v1 document: " + JsonProblem(exception));
                }
                if (pack is not null || errors.Count == 0) RulePackRules.Validate(pack, expectedId, errors);
                if (pack is not null && errors.Count == 0)
                {
                    foreach (var id in pack.Include.SelectMany(selector => selector.Ids ?? []).Where(id => !definitions.ContainsKey(id)))
                        errors.Add($"selects unknown rule id '{id}'.");
                    if (packs.TryGetValue(pack.Id, out var existing))
                        errors.Add($"id '{pack.Id}' is already defined by the {existing.Origin} rule pool.");
                }
                if (errors.Count > 0)
                {
                    diagnostics.AddRange(errors.Select(error => new RuleDiagnostic(scope.Scope, display, expectedId, error)));
                    continue;
                }
                packs[pack!.Id] = (pack, scope.Scope);
                Used(Path.Combine(scope.Directory, RuleScopeSources.PacksDirectoryName, fileName), scope.Scope);
            }
        }

        var applicability = new RuleApplicability(RuleScopes.Default, [RulePackRules.HouseStyleId], null);
        var applicabilityDocuments = new Dictionary<string, RuleApplicabilityDocument?>(StringComparer.Ordinal);
        foreach (var scope in new[] { sources.Global, sources.Project })
        {
            applicabilityDocuments[scope.Scope] = null;
            if (scope.Applicability is null) continue;
            var display = scope.Display(RuleScopeSources.ApplicabilityFileName);
            var errors = new List<string>();
            RuleApplicabilityDocument? document = null;
            try
            {
                document = JsonSerializer.Deserialize<RuleApplicabilityDocument>(scope.Applicability, RulePackRules.StrictJson);
            }
            catch (JsonException exception)
            {
                errors.Add("is not a valid rule-applicability.v1 document: " + JsonProblem(exception));
            }
            if (errors.Count == 0) ValidateApplicability(document, packs.ContainsKey, errors);
            if (errors.Count > 0)
            {
                diagnostics.AddRange(errors.Select(error => new RuleDiagnostic(scope.Scope, display, null, error)));
                continue;
            }
            applicabilityDocuments[scope.Scope] = document;
            applicability = new RuleApplicability(scope.Scope,
                document!.Packs.Distinct(StringComparer.Ordinal).ToArray(), document.Reason);
            Used(Path.Combine(scope.Directory, RuleScopeSources.ApplicabilityFileName), scope.Scope);
        }

        var overridesById = new Dictionary<string, (RuleOverride Override, string Source, string Scope)>(StringComparer.Ordinal);
        var overrideLists = new Dictionary<string, IReadOnlyList<RuleOverride>>(StringComparer.Ordinal);
        foreach (var (scope, scopeLabel, path, display, text) in new[]
                 {
                     (RuleScopes.Global, RuleScopes.Global,
                         Path.Combine(sources.Global.Directory, RuleScopeSources.OverridesFileName),
                         sources.Global.Display(RuleScopeSources.OverridesFileName), sources.Global.Overrides),
                     (RuleScopes.SharedGlobal, RuleScopes.Global, sources.SharedOverridesPath ?? string.Empty,
                         "global inputs/" + GlobalFileName, sources.SharedOverrides),
                     (RuleScopes.Project, RuleScopes.Project,
                         Path.Combine(sources.Project.Directory, RuleScopeSources.OverridesFileName),
                         sources.Project.Display(RuleScopeSources.OverridesFileName), sources.Project.Overrides),
                 })
        {
            overrideLists[scope] = [];
            if (text is null) continue;
            var document = ParseOverrides(text, display, scope, definitions.ContainsKey, diagnostics);
            if (document is null) continue;
            overrideLists[scope] = document.Overrides;
            foreach (var entry in document.Overrides) overridesById[entry.Id] = (entry, path, scopeLabel);
            Used(path, scopeLabel);
        }

        var applicablePacks = applicability.Packs
            .Where(packs.ContainsKey)
            .Select(id => packs[id].Pack)
            .ToArray();
        var resolved = definitions.Values
            .Where(definition => definition.Rule.Enabled)
            .Select(definition =>
            {
                var rule = definition.Rule;
                var selectedBy = applicablePacks.Where(pack => pack.Selects(rule)).Select(pack => pack.Id).ToArray();
                var hasOverride = overridesById.TryGetValue(rule.Id, out var found);
                var effectiveEnabled = hasOverride && found.Override.Enabled.HasValue
                    ? found.Override.Enabled.Value
                    : selectedBy.Length > 0;
                var severityOverridden = hasOverride && found.Override.Severity.HasValue;
                var effectiveSeverity = severityOverridden ? found.Override.Severity!.Value : rule.Severity;
                return new ResolvedRule(rule, effectiveEnabled, effectiveSeverity, severityOverridden,
                    hasOverride ? found.Source : RuleScopes.BuiltIn, hasOverride ? found.Override.Reason : null,
                    definition.Origin, selectedBy, hasOverride ? found.Scope : null);
            })
            .OrderBy(rule => rule.Rule.Id, StringComparer.Ordinal)
            .ToArray();

        var resolvedPacks = packs.Values
            .Select(pack => new ResolvedRulePack(pack.Pack, pack.Origin,
                resolved.Where(rule => pack.Pack.Selects(rule.Rule)).Select(rule => rule.Rule.Id).ToArray()))
            .OrderBy(pack => pack.Origin == RuleScopes.BuiltIn ? 0 : pack.Origin == RuleScopes.Global ? 1 : 2)
            .ThenBy(pack => pack.Pack.Id, StringComparer.Ordinal)
            .ToArray();

        var scopeStates = new Dictionary<string, RuleScopeState>(StringComparer.Ordinal)
        {
            [RuleScopes.Global] = new(RuleScopes.Global, overrideLists[RuleScopes.Global],
                applicabilityDocuments[RuleScopes.Global], customFiles[RuleScopes.Global], packFiles[RuleScopes.Global]),
            [RuleScopes.SharedGlobal] = new(RuleScopes.SharedGlobal, overrideLists[RuleScopes.SharedGlobal], null, [], []),
            [RuleScopes.Project] = new(RuleScopes.Project, overrideLists[RuleScopes.Project],
                applicabilityDocuments[RuleScopes.Project], customFiles[RuleScopes.Project], packFiles[RuleScopes.Project]),
        };

        return new ResolvedRuleCatalogue(builtIn.CatalogueVersion, resolved, sourcePaths)
        {
            Applicability = applicability,
            Packs = resolvedPacks,
            Diagnostics = diagnostics,
            SourceScopes = sourceScopes,
            ScopeStates = scopeStates,
        };
    }

    internal static void ValidateApplicability(RuleApplicabilityDocument? document, Func<string, bool> packExists,
        ICollection<string> errors)
    {
        if (document is null)
        {
            errors.Add("is empty.");
            return;
        }
        if (document.SchemaVersion != 1) errors.Add("must declare schemaVersion 1.");
        if (document.Packs is null) errors.Add("requires a packs list (it may be empty).");
        else
        {
            if (document.Packs.Count > RulePackRules.MaxPacksPerScope) errors.Add($"selects more than {RulePackRules.MaxPacksPerScope} packs.");
            if (document.Packs.Any(id => id is null)) errors.Add("packs must not contain null.");
            foreach (var id in document.Packs.Where(id => id is not null && !packExists(id))) errors.Add($"selects unknown pack '{id}'.");
        }
        if (string.IsNullOrWhiteSpace(document.Reason)) errors.Add("requires a reason.");
        else if (document.Reason.Length > 1000) errors.Add("reason must be at most 1000 characters.");
    }

    private static RuleOverrideDocument? ParseOverrides(string text, string display, string scope,
        Func<string, bool> ruleExists, List<RuleDiagnostic> diagnostics)
    {
        RuleOverrideDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RuleOverrideDocument>(text, AttackCoverageJson.Options);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new RuleDiagnostic(scope, display, null, "is not a valid rule-config.v1 document: " + JsonProblem(exception)));
            return null;
        }
        if (document is null || document.SchemaVersion != 1 || document.Overrides is null)
        {
            diagnostics.Add(new RuleDiagnostic(scope, display, null, "has an unsupported contract; expected schemaVersion 1 and an overrides list."));
            return null;
        }
        var before = diagnostics.Count;
        foreach (var entry in document.Overrides)
        {
            if (entry is null)
            {
                diagnostics.Add(new RuleDiagnostic(scope, display, null, "contains an empty override entry."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Id) || !ruleExists(entry.Id))
                diagnostics.Add(new RuleDiagnostic(scope, display, entry.Id, $"references unknown rule id '{entry.Id}'."));
            if (entry.Enabled is null && entry.Severity is null)
                diagnostics.Add(new RuleDiagnostic(scope, display, entry.Id, $"entry '{entry.Id}' must set enabled or severity."));
            if (string.IsNullOrWhiteSpace(entry.Reason))
                diagnostics.Add(new RuleDiagnostic(scope, display, entry.Id, $"entry '{entry.Id}' requires a reason."));
        }
        return diagnostics.Count == before ? document : null;
    }

    // Names the JSON location of a parse failure without echoing the file content back.
    private static string JsonProblem(JsonException exception) =>
        exception.Path is { Length: > 0 } path
            ? $"problem at {path}" + (exception.LineNumber is { } line ? $" (line {line + 1})" : string.Empty) + "."
            : exception.LineNumber is { } only ? $"malformed JSON at line {only + 1}." : "malformed JSON.";

    /// <summary>The <c>id:</c> frontmatter value of a rule file, read without validating the rest.</summary>
    public static string? PeekId(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Take(40))
        {
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                var value = line[3..].Trim();
                return value.Length is > 0 and <= 40 ? value : null;
            }
        }
        return null;
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

    // The embedded catalogue is immutable for the life of the process. Parse it once.
    private static readonly Lazy<RuleCatalogueDocument> BuiltIn = new(ReadBuiltIn, isThreadSafe: true);
    private static readonly Lazy<IReadOnlyList<RulePackDocument>> BuiltInPackList = new(ReadBuiltInPacks, isThreadSafe: true);

    private static Stream OpenResource(string suffix)
    {
        var assembly = typeof(RuleCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(suffix, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The built-in resource '{suffix}' is unavailable.");
    }

    private static RuleCatalogueDocument ReadBuiltIn()
    {
        using var stream = OpenResource(BuiltInResourceSuffix);
        var document = JsonSerializer.Deserialize<RuleCatalogueDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException("The built-in rule catalogue is empty.");
        Validate(document, "built-in");
        return document;
    }

    private static IReadOnlyList<RulePackDocument> ReadBuiltInPacks()
    {
        using var stream = OpenResource(BuiltInPacksResourceSuffix);
        var document = JsonSerializer.Deserialize<RulePackCatalogueDocument>(stream, RulePackRules.StrictJson)
            ?? throw new JsonException("The built-in rule pack catalogue is empty.");
        var knownIds = BuiltIn.Value.Entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var pack in document.Packs)
        {
            RulePackRules.Validate(pack, null, errors);
            errors.AddRange(pack.Include.SelectMany(selector => selector.Ids ?? [])
                .Where(id => !knownIds.Contains(id)).Select(id => $"pack '{pack.Id}' selects unknown rule '{id}'."));
        }
        if (document.SchemaVersion != 1 || errors.Count > 0 ||
            document.Packs.All(pack => pack.Id != RulePackRules.HouseStyleId) ||
            document.Packs.GroupBy(pack => pack.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("The built-in rule pack catalogue is invalid: " + string.Join(" ", errors));
        return document.Packs;
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
