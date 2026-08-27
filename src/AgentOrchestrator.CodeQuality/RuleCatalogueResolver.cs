using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Resolves the Quality Studio rule library (see rules/README.md): the built-in
/// catalogue compiled from rules/**/*.md, adjusted by a project's sparse
/// .quality/rules.json overrides. Modeled on AttackCatalogueResolver.
/// </summary>
public sealed class RuleCatalogueResolver
{
    public const string ProjectRelativePath = ".quality/rules.json";
    private const string BuiltInResourceSuffix = "catalogues.rule-catalogue.v1.json";

    public ResolvedRuleCatalogue Resolve(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var catalogue = ReadBuiltIn();
        ValidateCatalogue(catalogue, "built-in");
        if (catalogue.Entries.GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new JsonException("The built-in rule catalogue contains duplicate ids.");

        var byId = catalogue.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        var overrides = ReadOverrides(repositoryRoot);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in overrides)
        {
            if (!byId.ContainsKey(item.Id))
                throw new JsonException($"'{ProjectRelativePath}' overrides unknown rule id '{item.Id}'.");
            if (item.Enabled is null && item.Severity is null)
                throw new JsonException($"'{ProjectRelativePath}' override for '{item.Id}' sets neither enabled nor severity.");
            if (string.IsNullOrWhiteSpace(item.Reason))
                throw new JsonException($"'{ProjectRelativePath}' override for '{item.Id}' requires a non-empty reason.");
            reasons[item.Id] = item.Reason;
        }
        var overridesById = overrides.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        var resolved = catalogue.Entries
            .Where(entry => entry.Status == RuleStatus.Active)
            .Select(entry =>
            {
                overridesById.TryGetValue(entry.Id, out var over);
                var enabled = over?.Enabled ?? entry.Tier == RuleTier.Core;
                var severity = over?.Severity ?? entry.Severity;
                return new ResolvedRuleEntry(entry, enabled, severity, over is null ? null : reasons[entry.Id]);
            })
            .OrderBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        return new ResolvedRuleCatalogue(catalogue.CatalogueVersion, resolved);
    }

    private static IReadOnlyList<RuleOverride> ReadOverrides(string repositoryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot),
            ProjectRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<RulesConfigDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException($"'{ProjectRelativePath}' is empty.");
        if (document.SchemaVersion != 1) throw new JsonException($"'{ProjectRelativePath}' has an unsupported schemaVersion.");
        return document.Overrides ?? [];
    }

    private static RuleCatalogueDocument ReadBuiltIn()
    {
        var assembly = typeof(RuleCatalogueResolver).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith(BuiltInResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The built-in rule catalogue is unavailable.");
        return JsonSerializer.Deserialize<RuleCatalogueDocument>(stream, AttackCoverageJson.Options)
            ?? throw new JsonException("The built-in rule catalogue is empty.");
    }

    private static void ValidateCatalogue(RuleCatalogueDocument document, string source)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.CatalogueVersion) || document.Entries is null)
            throw new JsonException($"Rule catalogue '{source}' has an unsupported contract.");
        if (document.Entries.Any(entry =>
                string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version) ||
                string.IsNullOrWhiteSpace(entry.Title) || string.IsNullOrWhiteSpace(entry.Technology) ||
                string.IsNullOrWhiteSpace(entry.Category) || entry.Kinds is not { Count: > 0 } ||
                string.IsNullOrWhiteSpace(entry.Statement) || string.IsNullOrWhiteSpace(entry.Rationale) ||
                string.IsNullOrWhiteSpace(entry.GoodExample) || string.IsNullOrWhiteSpace(entry.BadExample)))
            throw new JsonException($"Rule catalogue '{source}' contains an invalid entry.");
    }
}
