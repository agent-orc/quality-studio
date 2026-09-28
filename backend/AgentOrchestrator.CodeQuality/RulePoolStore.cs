using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One line of the rule audit trail (<c>rule-audit.v1</c>).</summary>
public sealed record RuleAuditEntry(
    int SchemaVersion,
    DateTimeOffset At,
    string Actor,
    string Scope,
    string Action,
    string Target,
    string Reason,
    JsonNode? Before,
    JsonNode? After);

public sealed record RuleSetApplicability(IReadOnlyList<string> Packs, string Reason);

/// <summary>
/// Everything one writable scope contributes to the rule pool, as one portable file
/// (<c>rule-set.v1</c>): applicability, overrides, custom rules and custom packs.
/// </summary>
public sealed record RuleSetDocument(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    string? Name,
    string? LibraryVersion,
    DateTimeOffset? ExportedAt,
    string? Scope,
    string? Digest,
    RuleSetApplicability? Applicability,
    IReadOnlyList<RuleOverride> Overrides,
    IReadOnlyList<RuleDefinition> CustomRules,
    IReadOnlyList<RulePackDocument> Packs)
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/rule-set.v1.schema.json";
}

/// <summary>What an import changes, per entry: <c>added</c>, <c>updated</c>, <c>removed</c> or <c>unchanged</c>.</summary>
public sealed record RuleImportChange(string Kind, string Id, string Change);

public sealed record RuleImportResult(
    bool Valid,
    bool Applied,
    bool ModifiedSinceExport,
    IReadOnlyList<RuleImportChange> Changes,
    IReadOnlyList<RuleDiagnostic> Diagnostics,
    ResolvedRuleCatalogue Catalogue);

/// <summary>A rejected change. The message and diagnostics carry no absolute path and may be shown to a caller.</summary>
public sealed class RulePoolValidationException(string message, IReadOnlyList<RuleDiagnostic>? diagnostics = null)
    : Exception(message)
{
    public IReadOnlyList<RuleDiagnostic> Diagnostics { get; } = diagnostics ?? [];
}

/// <summary>A custom rule, pack or override that the change names does not exist in the scope.</summary>
public sealed class RulePoolEntryNotFoundException(string message) : Exception(message);

/// <summary>
/// The only write path into the rule pool. Every change is applied to an in-memory copy of the
/// pool first and resolved with <see cref="RuleCatalogueResolver.Build"/>; a change that would add
/// a configuration problem is rejected before a file is touched, so the management surface can
/// never leave reviews failing on a file it wrote. A change that repairs or leaves alone an
/// existing problem goes through, so a broken hand edit can be fixed from the same surface.
/// <para>
/// Project changes are written to <c>.quality/rules/</c> in the checkout, beside the code they
/// govern; global changes to <c>&lt;data root&gt;/rules/</c>. Each accepted change appends one
/// line to the scope's audit trail with the actor, the reason and the before and after state.
/// </para>
/// </summary>
public sealed class RulePoolStore
{
    public const string AuditFileName = "audit.jsonl";
    public const long AuditRotateBytes = 2 * 1024 * 1024;
    public const int MaxReasonLength = 1000;
    public const int MaxAuditEntries = 500;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuleCatalogueResolver resolver;
    private readonly TimeProvider time;

    public RulePoolStore(
        string repositoryRoot,
        string? globalInputsDirectory = null,
        string? globalRulesDirectory = null,
        TimeProvider? timeProvider = null)
    {
        resolver = new RuleCatalogueResolver(globalRulesDirectory);
        Locations = resolver.Locate(repositoryRoot, globalInputsDirectory);
        time = timeProvider ?? TimeProvider.System;
    }

    public RulePoolLocations Locations { get; }

    /// <summary>The current pool, with configuration problems reported rather than thrown.</summary>
    public ResolvedRuleCatalogue Inspect() => resolver.Inspect(Locations.RepositoryRoot,
        Locations.SharedOverridesPath is null ? null : Path.GetDirectoryName(Locations.SharedOverridesPath));

    public string AuditPath(string scope) => RequireScope(scope) == RuleScopes.Project
        ? QualityDataRoot.Combine(Locations.RepositoryRoot, "rules", AuditFileName)
        : Path.Combine(Locations.GlobalDirectory, AuditFileName);

    public ResolvedRuleCatalogue SetOverride(string scope, RuleOverride entry, string actor)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var reason = RequireReason(entry.Reason);
        if (entry.Enabled is null && entry.Severity is null)
            throw new RulePoolValidationException("An override must set enabled, severity, or both.");
        return Mutate(scope, actor, "override.set", entry.Id, reason, current =>
        {
            var overrides = ReadOverrideList(current);
            var before = overrides.FirstOrDefault(value => value.Id == entry.Id);
            var next = new RuleOverride(entry.Id, entry.Enabled, entry.Severity, reason);
            var list = before is null
                ? overrides.Append(next).ToArray()
                : overrides.Select(value => value.Id == entry.Id ? next : value).ToArray();
            return (current with { Overrides = RenderOverrides(list) }, Node(before), Node(next));
        });
    }

    public ResolvedRuleCatalogue RemoveOverride(string scope, string id, string reason, string actor)
    {
        var why = RequireReason(reason);
        return Mutate(scope, actor, "override.remove", id, why, current =>
        {
            var overrides = ReadOverrideList(current);
            var before = overrides.FirstOrDefault(value => value.Id == id)
                ?? throw new RulePoolEntryNotFoundException($"The {scope} scope has no override for '{id}'.");
            var list = overrides.Where(value => value.Id != id).ToArray();
            return (current with { Overrides = list.Length == 0 ? null : RenderOverrides(list) }, Node(before), null);
        });
    }

    /// <summary>
    /// Creates or replaces a custom rule. With <paramref name="dryRun"/> the rule is validated in the
    /// context of the whole pool (id collisions included) and nothing is written.
    /// </summary>
    public ResolvedRuleCatalogue PutCustomRule(string scope, string id, string content, string reason, string actor,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        var why = dryRun ? "validation" : RequireReason(reason);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
        var errors = new List<string>();
        var rule = RuleMarkdown.ParseCustom(normalized, id + ".md", errors);
        var display = RuleScopeSources.Empty(RequireScope(scope), string.Empty)
            .Display(RuleScopeSources.CustomDirectoryName + "/" + id + ".md");
        if (rule is null)
            throw new RulePoolValidationException("The custom rule is invalid.",
                errors.Select(error => new RuleDiagnostic(scope, display, id, error)).ToArray());
        if (!string.Equals(rule.Id, id, StringComparison.Ordinal))
            throw new RulePoolValidationException($"The rule declares id '{rule.Id}', not '{id}'.");
        return Mutate(scope, actor, "custom-rule.put", id, why, current =>
        {
            var existing = CustomRuleFile(current, id);
            var rules = new Dictionary<string, string>(current.CustomRules, StringComparer.Ordinal);
            if (existing is not null) rules.Remove(existing);
            rules[existing ?? id + ".md"] = normalized;
            return (current with { CustomRules = rules },
                existing is null ? null : RuleSummary(current.CustomRules[existing]), RuleSummary(normalized));
        }, dryRun);
    }

    /// <summary>Deletes a custom rule and, with it, the same scope's override of that rule.</summary>
    public ResolvedRuleCatalogue DeleteCustomRule(string scope, string id, string reason, string actor)
    {
        var why = RequireReason(reason);
        return Mutate(scope, actor, "custom-rule.delete", id, why, current =>
        {
            var existing = CustomRuleFile(current, id)
                ?? throw new RulePoolEntryNotFoundException($"The {scope} scope has no custom rule '{id}'.");
            var rules = new Dictionary<string, string>(current.CustomRules, StringComparer.Ordinal);
            rules.Remove(existing);
            var next = current with { CustomRules = rules };
            if (current.Overrides is not null)
            {
                var overrides = ReadOverrideList(current).Where(value => value.Id != id).ToArray();
                next = next with { Overrides = overrides.Length == 0 ? null : RenderOverrides(overrides) };
            }
            return (next, RuleSummary(current.CustomRules[existing]), null);
        });
    }

    public ResolvedRuleCatalogue PutPack(string scope, string id, RulePackDocument pack, string reason, string actor)
    {
        var why = RequireReason(reason);
        var errors = new List<string>();
        RulePackRules.Validate(pack, id, errors);
        if (errors.Count > 0)
            throw new RulePoolValidationException("The rule pack is invalid.",
                errors.Select(error => new RuleDiagnostic(scope, $"packs/{id}.json", id, error)).ToArray());
        var text = RenderPack(pack);
        return Mutate(scope, actor, "pack.put", id, why, current =>
        {
            var fileName = id + ".json";
            var packs = new Dictionary<string, string>(current.Packs, StringComparer.Ordinal) { [fileName] = text };
            return (current with { Packs = packs },
                current.Packs.TryGetValue(fileName, out var before) ? ContentSummary(id, before) : null,
                ContentSummary(id, text));
        });
    }

    public ResolvedRuleCatalogue DeletePack(string scope, string id, string reason, string actor)
    {
        var why = RequireReason(reason);
        return Mutate(scope, actor, "pack.delete", id, why, current =>
        {
            var fileName = id + ".json";
            if (!current.Packs.TryGetValue(fileName, out var before))
                throw new RulePoolEntryNotFoundException($"The {scope} scope has no rule pack '{id}'.");
            var packs = new Dictionary<string, string>(current.Packs, StringComparer.Ordinal);
            packs.Remove(fileName);
            return (current with { Packs = packs }, ContentSummary(id, before), null);
        });
    }

    public ResolvedRuleCatalogue SetApplicability(string scope, IReadOnlyList<string> packs, string reason, string actor)
    {
        ArgumentNullException.ThrowIfNull(packs);
        var why = RequireReason(reason);
        var document = new RuleApplicabilityDocument(RuleApplicabilityDocument.SchemaId, 1,
            packs.Select(pack => pack.Trim()).Distinct(StringComparer.Ordinal).ToArray(), why);
        return Mutate(scope, actor, "applicability.set", "applicability", why, current =>
            (current with { Applicability = Render(document) }, ApplicabilityNode(current.Applicability), Node(document)));
    }

    public ResolvedRuleCatalogue ClearApplicability(string scope, string reason, string actor)
    {
        var why = RequireReason(reason);
        return Mutate(scope, actor, "applicability.clear", "applicability", why, current =>
        {
            if (current.Applicability is null)
                throw new RulePoolEntryNotFoundException($"The {scope} scope declares no applicability.");
            return (current with { Applicability = null }, ApplicabilityNode(current.Applicability), null);
        });
    }

    /// <summary>
    /// Writes one scope's contribution as a rule set. A scope with configuration problems of its own
    /// is refused: an export must be importable.
    /// </summary>
    public RuleSetDocument Export(string scope, string? name = null)
    {
        RequireScope(scope);
        var catalogue = RuleCatalogueResolver.Build(RulePoolSources.Read(Locations));
        var problems = catalogue.Diagnostics.Where(diagnostic => diagnostic.Scope == scope).ToArray();
        if (problems.Length > 0)
            throw new RulePoolValidationException($"The {scope} rule scope has problems; repair them before exporting.", problems);
        var state = catalogue.ScopeStates[scope];
        var customRules = state.CustomRules
            .Select(file => RuleMarkdown.ParseCustom(file.Content, file.FileName, new List<string>())!)
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
        var packs = state.Packs
            .Select(file => JsonSerializer.Deserialize<RulePackDocument>(file.Content, RulePackRules.StrictJson)!)
            .OrderBy(pack => pack.Id, StringComparer.Ordinal)
            .ToArray();
        var applicability = state.Applicability is null
            ? null
            : new RuleSetApplicability(state.Applicability.Packs, state.Applicability.Reason);
        return new RuleSetDocument(RuleSetDocument.SchemaId, 1, string.IsNullOrWhiteSpace(name) ? scope : name.Trim(),
            catalogue.CatalogueVersion, time.GetUtcNow(), scope,
            Digest(applicability, state.Overrides, customRules, packs),
            applicability, state.Overrides, customRules, packs);
    }

    /// <summary>
    /// Applies a rule set to one scope. <c>merge</c> adds or replaces entries by id and keeps the
    /// rest; <c>replace</c> makes the scope hold exactly the set. <paramref name="dryRun"/> reports
    /// the plan and its validity without writing.
    /// </summary>
    public RuleImportResult Import(string scope, RuleSetDocument set, string mode, bool dryRun, string reason, string actor)
    {
        RequireScope(scope);
        ArgumentNullException.ThrowIfNull(set);
        var why = dryRun && string.IsNullOrWhiteSpace(reason) ? "dry run" : RequireReason(reason);
        if (mode is not ("merge" or "replace"))
            throw new RulePoolValidationException("Import mode must be 'merge' or 'replace'.");
        var diagnostics = ValidateSet(scope, set);
        var overrides = set.Overrides ?? [];
        var customRules = set.CustomRules ?? [];
        var packs = set.Packs ?? [];
        var applicability = set.Applicability;
        var modified = set.Digest is not null &&
                       !string.Equals(set.Digest, Digest(applicability, overrides, customRules, packs), StringComparison.Ordinal);

        lock (Gate(scope))
        {
            var sources = RulePoolSources.Read(Locations);
            var current = sources.Scope(scope);
            if (diagnostics.Count > 0)
            {
                if (dryRun) return new RuleImportResult(false, false, modified, [], diagnostics, RuleCatalogueResolver.Build(sources));
                throw new RulePoolValidationException("The rule set is invalid.", diagnostics);
            }

            var currentOverrides = mode == "replace" && current.Overrides is not null && !TryReadOverrideList(current, out _)
                ? []
                : ReadOverrideList(current);
            var nextOverrides = mode == "replace"
                ? overrides.ToArray()
                : currentOverrides.Where(value => overrides.All(entry => entry.Id != value.Id)).Concat(overrides).ToArray();
            var nextRules = mode == "replace"
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(current.CustomRules, StringComparer.Ordinal);
            foreach (var rule in customRules)
            {
                var existing = CustomRuleFile(current, rule.Id);
                if (existing is not null) nextRules.Remove(existing);
                nextRules[existing ?? rule.Id + ".md"] = RuleMarkdown.Render(rule);
            }
            var nextPacks = mode == "replace"
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(current.Packs, StringComparer.Ordinal);
            foreach (var pack in packs) nextPacks[pack.Id + ".json"] = RenderPack(pack);
            var nextApplicability = applicability is not null
                ? Render(new RuleApplicabilityDocument(RuleApplicabilityDocument.SchemaId, 1, applicability.Packs, applicability.Reason))
                : mode == "replace" ? null : current.Applicability;
            var next = current with
            {
                Overrides = nextOverrides.Length == 0 ? null : RenderOverrides(nextOverrides),
                CustomRules = nextRules,
                Packs = nextPacks,
                Applicability = nextApplicability,
            };

            var changes = PlanChanges(current, next, currentOverrides, nextOverrides);
            var candidate = sources.With(next);
            var introduced = Introduced(sources, candidate, out var catalogue);
            if (introduced.Count > 0)
            {
                if (dryRun) return new RuleImportResult(false, false, modified, changes, introduced, catalogue);
                throw new RulePoolValidationException("The rule set would make the rule pool invalid.", introduced);
            }
            if (dryRun || changes.All(change => change.Change == "unchanged"))
                return new RuleImportResult(true, false, modified, changes, [], catalogue);

            Write(current, next);
            AppendAudit(scope, actor, "rule-set.import", string.IsNullOrWhiteSpace(set.Name) ? "rule set" : set.Name.Trim(), why,
                null, new JsonObject
                {
                    ["mode"] = mode,
                    ["digest"] = Digest(applicability, overrides, customRules, packs),
                    ["modifiedSinceExport"] = modified,
                    ["libraryVersion"] = set.LibraryVersion,
                    ["changes"] = JsonSerializer.SerializeToNode(changes.Where(change => change.Change != "unchanged").ToArray(),
                        AttackCoverageJson.Options),
                });
            return new RuleImportResult(true, true, modified, changes, [], catalogue);
        }
    }

    /// <summary>The newest audit entries of a scope, newest first.</summary>
    public IReadOnlyList<RuleAuditEntry> ReadAudit(string scope, int limit = 100)
    {
        var path = AuditPath(scope);
        if (!File.Exists(path)) return [];
        var entries = new List<RuleAuditEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize<RuleAuditEntry>(line, AttackCoverageJson.Options) is { } entry) entries.Add(entry);
            }
            catch (JsonException)
            {
                // A torn last line after a crash must not hide every entry before it.
            }
        }
        return entries.AsEnumerable().Reverse().Take(Math.Clamp(limit, 1, MaxAuditEntries)).ToArray();
    }

    /// <summary>Reads a rule set strictly: an unknown property is an error, not a silently dropped setting.</summary>
    public static RuleSetDocument ParseRuleSet(JsonElement element) =>
        ParseStrict<RuleSetDocument>(element, "rule-set.v1");

    /// <summary>Reads a rule pack strictly.</summary>
    public static RulePackDocument ParsePack(JsonElement element) =>
        ParseStrict<RulePackDocument>(element, "rule-pack.v1");

    private static T ParseStrict<T>(JsonElement element, string contract) where T : class
    {
        try
        {
            return element.Deserialize<T>(RulePackRules.StrictJson)
                ?? throw new RulePoolValidationException($"The {contract} document is empty.");
        }
        catch (JsonException exception)
        {
            throw new RulePoolValidationException($"The document is not a valid {contract} document" +
                (exception.Path is { Length: > 0 } path ? $": problem at {path}." : "."));
        }
    }

    public static string Digest(RuleSetApplicability? applicability, IReadOnlyList<RuleOverride> overrides,
        IReadOnlyList<RuleDefinition> customRules, IReadOnlyList<RulePackDocument> packs)
    {
        var canonical = JsonSerializer.Serialize(new { applicability, overrides, customRules, packs }, AttackCoverageJson.Options);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private ResolvedRuleCatalogue Mutate(string scope, string actor, string action, string target, string reason,
        Func<RuleScopeSources, (RuleScopeSources Next, JsonNode? Before, JsonNode? After)> change, bool dryRun = false)
    {
        RequireScope(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        lock (Gate(scope))
        {
            var sources = RulePoolSources.Read(Locations);
            var current = sources.Scope(scope);
            var (next, before, after) = change(current);
            var candidate = sources.With(next);
            var introduced = Introduced(sources, candidate, out var catalogue);
            if (introduced.Count > 0)
                throw new RulePoolValidationException("The change would make the rule pool invalid.", introduced);
            if (dryRun || Same(current, next)) return catalogue;
            Write(current, next);
            AppendAudit(scope, actor, action, target, reason, before, after);
            return catalogue;
        }
    }

    private static List<RuleDiagnostic> Introduced(RulePoolSources current, RulePoolSources candidate,
        out ResolvedRuleCatalogue catalogue)
    {
        var existing = RuleCatalogueResolver.Build(current).Diagnostics.ToHashSet();
        catalogue = RuleCatalogueResolver.Build(candidate);
        return catalogue.Diagnostics.Where(diagnostic => !existing.Contains(diagnostic)).ToList();
    }

    private void Write(RuleScopeSources current, RuleScopeSources next)
    {
        var directory = next.Directory;
        void Put(string relative, string? before, string? after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal)) return;
            var path = Path.Combine(directory, relative);
            if (after is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                AtomicFile.WriteAllText(path, after);
            }
        }
        Put(RuleScopeSources.OverridesFileName, current.Overrides, next.Overrides);
        Put(RuleScopeSources.ApplicabilityFileName, current.Applicability, next.Applicability);
        foreach (var (folder, before, after) in new[]
                 {
                     (RuleScopeSources.CustomDirectoryName, current.CustomRules, next.CustomRules),
                     (RuleScopeSources.PacksDirectoryName, current.Packs, next.Packs),
                 })
        {
            foreach (var name in before.Keys.Union(after.Keys, StringComparer.Ordinal))
            {
                RequireSafeFileName(name);
                Put(Path.Combine(folder, name), before.GetValueOrDefault(name), after.GetValueOrDefault(name));
            }
        }
        RuleCatalogueResolver.Invalidate();
    }

    private void AppendAudit(string scope, string actor, string action, string target, string reason,
        JsonNode? before, JsonNode? after)
    {
        var path = AuditPath(scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = new FileInfo(path);
        // Rotated, never truncated: an archive keeps its history, and the live file stays small
        // enough to read on every request.
        if (info.Exists && info.Length > AuditRotateBytes)
            File.Move(path, Path.Combine(info.DirectoryName!,
                $"audit-{time.GetUtcNow().ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)}.jsonl"));
        var entry = new RuleAuditEntry(1, time.GetUtcNow(), actor, scope, action, target, reason, before, after);
        var line = JsonSerializer.Serialize(entry, AuditJson) + "\n";
        File.AppendAllText(path, line, new UTF8Encoding(false));
    }

    private static List<RuleImportChange> PlanChanges(RuleScopeSources current, RuleScopeSources next,
        IReadOnlyList<RuleOverride> currentOverrides, IReadOnlyList<RuleOverride> nextOverrides)
    {
        var changes = new List<RuleImportChange>();
        void Compare<T>(string kind, IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after,
            Func<T, T, bool> equal)
        {
            foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var had = before.TryGetValue(id, out var left);
                var has = after.TryGetValue(id, out var right);
                changes.Add(new RuleImportChange(kind, id,
                    !had ? "added" : !has ? "removed" : equal(left!, right!) ? "unchanged" : "updated"));
            }
        }
        Compare("override",
            currentOverrides.GroupBy(value => value.Id).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal),
            nextOverrides.GroupBy(value => value.Id).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal),
            (left, right) => left == right);
        Compare("custom-rule", ById(current.CustomRules), ById(next.CustomRules), string.Equals);
        Compare("pack", current.Packs.ToDictionary(pair => Path.GetFileNameWithoutExtension(pair.Key), pair => pair.Value),
            next.Packs.ToDictionary(pair => Path.GetFileNameWithoutExtension(pair.Key), pair => pair.Value), string.Equals);
        if (current.Applicability is not null || next.Applicability is not null)
            changes.Add(new RuleImportChange("applicability", "applicability",
                current.Applicability is null ? "added"
                : next.Applicability is null ? "removed"
                : string.Equals(current.Applicability, next.Applicability, StringComparison.Ordinal) ? "unchanged" : "updated"));
        return changes;
    }

    private static Dictionary<string, string> ById(IReadOnlyDictionary<string, string> files) =>
        files.GroupBy(pair => RuleCatalogueResolver.PeekId(pair.Value) ?? pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    private List<RuleDiagnostic> ValidateSet(string scope, RuleSetDocument set)
    {
        var diagnostics = new List<RuleDiagnostic>();
        void Add(string? subject, string message) => diagnostics.Add(new RuleDiagnostic(scope, "rule set", subject, message));
        if (set.SchemaVersion != 1) Add(null, "must declare schemaVersion 1.");
        if (set.Overrides is null || set.CustomRules is null || set.Packs is null)
            Add(null, "requires overrides, customRules and packs lists (each may be empty).");
        if (set.CustomRules?.Count > RuleScopeSources.MaxCustomRules) Add(null, $"holds more than {RuleScopeSources.MaxCustomRules} custom rules.");
        if (set.Packs?.Count > RulePackRules.MaxPacksPerScope) Add(null, $"holds more than {RulePackRules.MaxPacksPerScope} packs.");
        foreach (var group in (set.CustomRules ?? []).Where(rule => rule is not null).GroupBy(rule => rule.Id).Where(group => group.Count() > 1))
            Add(group.Key, $"defines custom rule '{group.Key}' more than once.");
        foreach (var group in (set.Packs ?? []).Where(pack => pack is not null).GroupBy(pack => pack.Id).Where(group => group.Count() > 1))
            Add(group.Key, $"defines pack '{group.Key}' more than once.");
        foreach (var rule in set.CustomRules ?? [])
        {
            if (rule is null)
            {
                Add(null, "contains an empty custom rule.");
                continue;
            }
            var errors = new List<string>();
            RuleMarkdown.ValidateStructured(rule, errors);
            diagnostics.AddRange(errors.Select(error => new RuleDiagnostic(scope, "rule set", rule.Id, "custom rule " + error)));
        }
        foreach (var pack in set.Packs ?? [])
        {
            var errors = new List<string>();
            RulePackRules.Validate(pack, null, errors);
            diagnostics.AddRange(errors.Select(error => new RuleDiagnostic(scope, "rule set", pack?.Id, "pack " + error)));
        }
        foreach (var entry in set.Overrides ?? [])
        {
            if (entry is null) Add(null, "contains an empty override.");
            else if (entry.Reason?.Length > MaxReasonLength) Add(entry.Id, $"override reason must be at most {MaxReasonLength} characters.");
        }
        if (set.Applicability is { } applicability &&
            (applicability.Packs is null || string.IsNullOrWhiteSpace(applicability.Reason)))
            Add("applicability", "applicability requires a packs list and a reason.");
        return diagnostics;
    }

    private static bool Same(RuleScopeSources left, RuleScopeSources right) =>
        string.Equals(left.Overrides, right.Overrides, StringComparison.Ordinal) &&
        string.Equals(left.Applicability, right.Applicability, StringComparison.Ordinal) &&
        SameFiles(left.CustomRules, right.CustomRules) &&
        SameFiles(left.Packs, right.Packs);

    private static bool SameFiles(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static string? CustomRuleFile(RuleScopeSources scope, string id) =>
        scope.CustomRules.Keys.FirstOrDefault(name =>
            name == id + ".md" || name.StartsWith(id + "-", StringComparison.Ordinal) ||
            RuleCatalogueResolver.PeekId(scope.CustomRules[name]) == id);

    private static IReadOnlyList<RuleOverride> ReadOverrideList(RuleScopeSources scope) =>
        TryReadOverrideList(scope, out var overrides)
            ? overrides
            : throw new RulePoolValidationException(
                $"{scope.Display(RuleScopeSources.OverridesFileName)} cannot be read. Repair the file, or replace it by importing a rule set in 'replace' mode.");

    private static bool TryReadOverrideList(RuleScopeSources scope, out IReadOnlyList<RuleOverride> overrides)
    {
        overrides = [];
        if (scope.Overrides is null) return true;
        try
        {
            var document = JsonSerializer.Deserialize<RuleOverrideDocument>(scope.Overrides, AttackCoverageJson.Options);
            if (document is not { SchemaVersion: 1, Overrides: not null }) return false;
            overrides = document.Overrides.Where(entry => entry is not null).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RenderOverrides(IReadOnlyList<RuleOverride> overrides) =>
        Render(new RuleOverrideDocument(RuleOverrideDocument.SchemaId, 1, overrides));

    private static string RenderPack(RulePackDocument pack) =>
        Render(pack with { Schema = RulePackDocument.SchemaId });

    private static string Render<T>(T document) => JsonSerializer.Serialize(document, AttackCoverageJson.Options) + "\n";

    private static JsonNode? Node<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.SerializeToNode(value, AttackCoverageJson.Options);

    private static JsonNode? ApplicabilityNode(string? text)
    {
        if (text is null) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create("unreadable applicability file");
        }
    }

    private static JsonObject RuleSummary(string content)
    {
        var rule = RuleMarkdown.ParseCustom(content, RuleCatalogueResolver.PeekId(content) + ".md", new List<string>());
        return new JsonObject
        {
            ["id"] = rule?.Id ?? RuleCatalogueResolver.PeekId(content),
            ["version"] = rule?.Version,
            ["title"] = rule?.Title,
            ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
        };
    }

    private static JsonObject ContentSummary(string id, string content) => new()
    {
        ["id"] = id,
        ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
    };

    private object Gate(string scope) => Gates.GetOrAdd(
        scope == RuleScopes.Project ? Locations.ProjectDirectory : Locations.GlobalDirectory, _ => new object());

    private static string RequireScope(string scope) => RuleScopes.IsWritable(scope)
        ? scope
        : throw new RulePoolValidationException("Scope must be 'project' or 'global'.");

    private static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new RulePoolValidationException("A reason is required; it is recorded in the audit trail.");
        if (trimmed.Length > MaxReasonLength)
            throw new RulePoolValidationException($"A reason must be at most {MaxReasonLength} characters.");
        return trimmed;
    }

    // File names come from validated ids or from enumerating the scope folder; this is the last
    // guard before a write or delete, so a name can never leave the folder.
    private static void RequireSafeFileName(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal))
            throw new RulePoolValidationException($"'{name}' is not a valid rule file name.");
    }

    private static readonly JsonSerializerOptions AuditJson = new(AttackCoverageJson.Options) { WriteIndented = false };
}
