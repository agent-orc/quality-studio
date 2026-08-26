using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRuleExamples(string Bad, string Good);

public sealed record QualityRuleHistoryEntry(string Version, string Date, string Change);

public sealed record QualityRule(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    string Id,
    string Version,
    string Title,
    string Language,
    string Category,
    string Statement,
    string Rationale,
    string Severity,
    bool Autofixable,
    bool DefaultEnabled,
    IReadOnlyList<string> ReviewKinds,
    IReadOnlyList<string> AppliesTo,
    QualityRuleExamples Examples,
    IReadOnlyList<QualityRuleHistoryEntry> History);

public sealed record QualityRuleOverride(bool? Enabled = null, string? Severity = null);

public sealed record QualityRulesConfiguration(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    IReadOnlyDictionary<string, QualityRuleOverride> Overrides);

public sealed record EffectiveQualityRule(QualityRule Definition, bool Enabled, string Severity)
{
    public string PromptContent => $"""
        ### {Definition.Id} — {Definition.Title}
        Version: {Definition.Version}; severity: {Severity}; autofixable: {Definition.Autofixable.ToString().ToLowerInvariant()}

        Statement: {Definition.Statement}

        Rationale: {Definition.Rationale}

        Bad example:
        ```{ExampleFence(Definition.Language)}
        {Definition.Examples.Bad}
        ```

        Good example:
        ```{ExampleFence(Definition.Language)}
        {Definition.Examples.Good}
        ```
        """;

    private static string ExampleFence(string language) => language switch
    {
        "angular" => "typescript",
        "csharp" => "csharp",
        _ => "text",
    };
}

public sealed record QualityRuleResolution(
    IReadOnlyList<EffectiveQualityRule> Rules,
    string ConfigurationPath)
{
    public string PromptContext => Rules.Count == 0
        ? "(no named Quality Studio rules apply to this review target)"
        : string.Join("\n\n", Rules.Select(rule => rule.PromptContent));

    public IReadOnlyList<ReviewInput> AsReviewInputs() => Rules.Select(rule => new ReviewInput(
        rule.Definition.Id,
        $"embedded:rules/{rule.Definition.Language}/{rule.Definition.Id}.json",
        "rule",
        0,
        rule.Definition.ReviewKinds,
        ["all"],
        rule.Enabled,
        rule.PromptContent,
        rule.PromptContent,
        false)).ToArray();
}

/// <summary>
/// Loads Quality Studio's versioned, embedded rule catalogue and applies repository-owned
/// overrides from .quality/rules.json. Rules remain file-first under the repository's rules/ tree.
/// </summary>
public sealed partial class RuleLibrary
{
    public const string RuleSchema = "https://quality.studio/schemas/quality-rule.v1.schema.json";
    public const string ConfigurationSchema = "https://quality.studio/schemas/quality-rules-config.v1.schema.json";
    public const string ConfigurationRelativePath = ".quality/rules.json";
    private static readonly HashSet<string> Severities = ["critical", "high", "medium", "low", "info"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly IReadOnlyList<QualityRule> rules;
    private readonly ConcurrentDictionary<string, CachedConfiguration> configurationCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public RuleLibrary() : this(LoadEmbedded())
    {
    }

    public RuleLibrary(IEnumerable<QualityRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = rules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
        ValidateCatalogue(this.rules);
    }

    public IReadOnlyList<QualityRule> List() => rules;

    public QualityRuleResolution Resolve(
        string repositoryRoot,
        string reviewKind,
        IReadOnlyList<string> subjectPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewKind);
        ArgumentNullException.ThrowIfNull(subjectPaths);
        var root = Path.GetFullPath(repositoryRoot);
        var configurationPath = Path.Combine(root, ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var configuration = LoadConfiguration(configurationPath);
        foreach (var id in configuration.Overrides.Keys)
        {
            if (!rules.Any(rule => string.Equals(rule.Id, id, StringComparison.Ordinal)))
                throw new RuleFormatException($"Rule override '{id}' does not match a rule in the Quality Studio catalogue.");
        }

        var effective = new List<EffectiveQualityRule>();
        foreach (var rule in rules)
        {
            configuration.Overrides.TryGetValue(rule.Id, out var ruleOverride);
            var enabled = ruleOverride?.Enabled ?? rule.DefaultEnabled;
            var severity = ruleOverride?.Severity ?? rule.Severity;
            if (!Severities.Contains(severity))
                throw new RuleFormatException($"Rule override '{rule.Id}' has unsupported severity '{severity}'.");
            if (!enabled || !rule.ReviewKinds.Contains(reviewKind, StringComparer.OrdinalIgnoreCase) ||
                !subjectPaths.Any(path => rule.AppliesTo.Any(pattern => GlobMatches(pattern, Normalize(path)))))
                continue;
            effective.Add(new EffectiveQualityRule(rule, true, severity));
        }

        return new QualityRuleResolution(effective, configurationPath);
    }

    public ResolvedInputs AddToReviewInputs(ResolvedInputs inputs, QualityRuleResolution resolution)
    {
        var ruleInputs = resolution.AsReviewInputs();
        return inputs with
        {
            Inputs = inputs.Inputs.Concat(ruleInputs).ToArray(),
        };
    }

    private QualityRulesConfiguration LoadConfiguration(string path)
    {
        if (!File.Exists(path))
        {
            configurationCache.TryRemove(path, out _);
            return new QualityRulesConfiguration(ConfigurationSchema, 1,
                new Dictionary<string, QualityRuleOverride>(StringComparer.Ordinal));
        }
        var info = new FileInfo(path);
        var stamp = new ConfigurationStamp(info.LastWriteTimeUtc.Ticks, info.Length);
        if (configurationCache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Configuration;
        try
        {
            var configuration = JsonSerializer.Deserialize<QualityRulesConfiguration>(File.ReadAllText(path), JsonOptions)
                ?? throw new RuleFormatException($"Rule configuration '{path}' must contain a JSON object.");
            if (configuration.SchemaVersion != 1 ||
                !string.Equals(configuration.Schema, ConfigurationSchema, StringComparison.Ordinal))
                throw new RuleFormatException($"Rule configuration '{path}' uses an unsupported schema.");
            if (configuration.Overrides is null)
                throw new RuleFormatException($"Rule configuration '{path}' requires an overrides object.");
            var normalized = configuration with
            {
                Overrides = new Dictionary<string, QualityRuleOverride>(configuration.Overrides, StringComparer.Ordinal),
            };
            configurationCache[path] = new CachedConfiguration(stamp, normalized);
            return normalized;
        }
        catch (JsonException exception)
        {
            throw new RuleFormatException($"Rule configuration '{path}' is invalid JSON: {exception.Message}", exception);
        }
    }

    private static IEnumerable<QualityRule> LoadEmbedded()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".rules.", StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            QualityRule rule;
            try
            {
                rule = JsonSerializer.Deserialize<QualityRule>(stream, JsonOptions)
                    ?? throw new RuleFormatException($"Embedded rule '{resource}' must contain a JSON object.");
            }
            catch (JsonException exception)
            {
                throw new RuleFormatException($"Embedded rule '{resource}' is invalid: {exception.Message}", exception);
            }
            yield return rule;
        }
    }

    private static void ValidateCatalogue(IReadOnlyList<QualityRule> values)
    {
        if (values.Count == 0) throw new RuleFormatException("The Quality Studio rule catalogue is empty.");
        var duplicate = values.GroupBy(rule => rule.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new RuleFormatException($"Rule id '{duplicate.Key}' is duplicated.");
        foreach (var rule in values)
        {
            if (!string.Equals(rule.Schema, RuleSchema, StringComparison.Ordinal) || rule.SchemaVersion != 1)
                throw new RuleFormatException($"Rule '{rule.Id}' uses an unsupported schema.");
            if (!RuleIdPattern().IsMatch(rule.Id))
                throw new RuleFormatException($"Rule id '{rule.Id}' must match QS-<language>-<number>.");
            if (string.IsNullOrWhiteSpace(rule.Version) || string.IsNullOrWhiteSpace(rule.Title) ||
                string.IsNullOrWhiteSpace(rule.Language) || string.IsNullOrWhiteSpace(rule.Category) ||
                string.IsNullOrWhiteSpace(rule.Statement) || string.IsNullOrWhiteSpace(rule.Rationale))
                throw new RuleFormatException($"Rule '{rule.Id}' has a missing required text field.");
            if (!Severities.Contains(rule.Severity))
                throw new RuleFormatException($"Rule '{rule.Id}' has unsupported severity '{rule.Severity}'.");
            if (rule.ReviewKinds.Count == 0 || rule.AppliesTo.Count == 0 || rule.History.Count == 0)
                throw new RuleFormatException($"Rule '{rule.Id}' requires reviewKinds, appliesTo, and history.");
            if (rule.Examples is null || string.IsNullOrWhiteSpace(rule.Examples.Bad) || string.IsNullOrWhiteSpace(rule.Examples.Good))
                throw new RuleFormatException($"Rule '{rule.Id}' requires good and bad examples.");
            if (!rule.History.Any(entry => string.Equals(entry.Version, rule.Version, StringComparison.Ordinal)))
                throw new RuleFormatException($"Rule '{rule.Id}' history must include current version '{rule.Version}'.");
        }
    }

    private static bool GlobMatches(string glob, string path)
    {
        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    [GeneratedRegex("^QS-[A-Z]{2,8}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    private readonly record struct ConfigurationStamp(long LastWriteTicks, long Length);
    private sealed record CachedConfiguration(ConfigurationStamp Stamp, QualityRulesConfiguration Configuration);
}

public sealed class RuleFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
