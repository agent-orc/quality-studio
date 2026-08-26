using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRuleExample(string Description, string Code);

public sealed record QualityRuleHistoryEntry(string Version, string Date, string Change);

public sealed record QualityRuleDeterministicCheck(string Check);

public sealed record QualityRuleDefinition
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-rule.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Language { get; init; }
    public required string Category { get; init; }
    public required bool DefaultOn { get; init; }
    public required string Statement { get; init; }
    public required string Rationale { get; init; }
    public required QualityRuleExample BadExample { get; init; }
    public required QualityRuleExample GoodExample { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required bool Autofixable { get; init; }
    public QualityRuleDeterministicCheck? Deterministic { get; init; }
    public required IReadOnlyList<QualityRuleHistoryEntry> History { get; init; }
}

public sealed record QualityRuleScope(
    IReadOnlyList<string>? Languages = null,
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? Exclude = null);

public sealed record QualityRuleOverride(
    string RuleId,
    bool? Enabled = null,
    FindingSeverity? Severity = null,
    bool? Autofixable = null,
    QualityRuleScope? Scope = null,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);

public sealed record QualityRuleConfiguration
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-rules-config.v1.schema.json";
    public const string RelativePath = ".quality/rules.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public IReadOnlyList<QualityRuleOverride> Overrides { get; init; } = [];
}

public sealed record EffectiveQualityRule(
    QualityRuleDefinition Definition,
    FindingSeverity Severity,
    bool Autofixable,
    IReadOnlyDictionary<string, JsonElement> Parameters);

public sealed record ResolvedQualityRules(
    IReadOnlyList<EffectiveQualityRule> Rules,
    string EffectiveHash)
{
    public string PromptContext()
    {
        if (Rules.Count == 0) return "(no named Quality Studio rules apply to this subject)";
        var text = new StringBuilder(
            "These rules are authoritative named review context. When a finding is caused by one of them, " +
            "use its exact stable id as `ruleId`; do not invent a different id.\n");
        foreach (var effective in Rules)
        {
            var rule = effective.Definition;
            text.Append("\n### ").Append(rule.Id).Append(" — ").AppendLine(rule.Name)
                .Append("Severity: ").Append(effective.Severity.ToString().ToLowerInvariant())
                .Append(". Autofixable: ").Append(effective.Autofixable ? "yes" : "no").AppendLine(".")
                .Append("Statement: ").AppendLine(rule.Statement)
                .Append("Rationale: ").AppendLine(rule.Rationale)
                .Append("Bad example (\"").Append(rule.BadExample.Description).AppendLine("\"):")
                .AppendLine("```").AppendLine(rule.BadExample.Code.Trim()).AppendLine("```")
                .Append("Good example (\"").Append(rule.GoodExample.Description).AppendLine("\"):")
                .AppendLine("```").AppendLine(rule.GoodExample.Code.Trim()).AppendLine("```");
        }
        return text.ToString().TrimEnd();
    }
}

public sealed partial class QualityRuleLibrary
{
    private static readonly HashSet<string> Languages = ["angular", "csharp"];
    private readonly string libraryRoot;

    public QualityRuleLibrary(string libraryRoot)
    {
        this.libraryRoot = Path.GetFullPath(libraryRoot);
    }

    public IReadOnlyList<QualityRuleDefinition> Load()
    {
        if (!Directory.Exists(libraryRoot))
            throw new DirectoryNotFoundException($"Quality rule library does not exist: {libraryRoot}");
        var rules = Directory.EnumerateFiles(libraryRoot, "*.rule.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(LoadRule)
            .ToArray();
        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new QualityRuleFormatException($"Duplicate quality rule id '{duplicate.Key}'.");
        return rules;
    }

    private static QualityRuleDefinition LoadRule(string path)
    {
        QualityRuleDefinition rule;
        try
        {
            rule = JsonSerializer.Deserialize<QualityRuleDefinition>(File.ReadAllText(path), JsonOptions())
                   ?? throw new QualityRuleFormatException($"Rule '{path}' must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new QualityRuleFormatException($"Rule '{path}' is not valid quality-rule JSON.", exception);
        }
        Validate(rule, path);
        return rule;
    }

    private static void Validate(QualityRuleDefinition rule, string path)
    {
        if (rule.Schema != QualityRuleDefinition.SchemaId || rule.SchemaVersion != QualityRuleDefinition.CurrentSchemaVersion)
            throw new QualityRuleFormatException($"Rule '{path}' uses an unsupported schema.");
        if (!RuleIdPattern().IsMatch(rule.Id ?? string.Empty))
            throw new QualityRuleFormatException($"Rule '{path}' has an invalid stable id.");
        if (!string.Equals(Path.GetFileName(path), rule.Id + ".rule.json", StringComparison.Ordinal))
            throw new QualityRuleFormatException($"Rule file name must match its stable id '{rule.Id}'.");
        if (!Languages.Contains(rule.Language ?? string.Empty))
            throw new QualityRuleFormatException($"Rule '{rule.Id}' has unsupported language '{rule.Language}'.");
        if (new[] { rule.Name, rule.Category, rule.Statement, rule.Rationale }.Any(string.IsNullOrWhiteSpace))
            throw new QualityRuleFormatException($"Rule '{rule.Id}' is missing required explanatory text.");
        if (rule.BadExample is null || rule.GoodExample is null ||
            string.IsNullOrWhiteSpace(rule.BadExample.Description) || string.IsNullOrWhiteSpace(rule.BadExample.Code) ||
            string.IsNullOrWhiteSpace(rule.GoodExample.Description) || string.IsNullOrWhiteSpace(rule.GoodExample.Code))
            throw new QualityRuleFormatException($"Rule '{rule.Id}' requires described good and bad examples.");
        if (rule.History is not { Count: > 0 } || rule.History.Any(entry =>
                !VersionPattern().IsMatch(entry.Version ?? string.Empty) || string.IsNullOrWhiteSpace(entry.Change) ||
                !DateOnly.TryParseExact(entry.Date, "yyyy-MM-dd", out _)))
            throw new QualityRuleFormatException($"Rule '{rule.Id}' requires valid change history entries.");
    }

    internal static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    [GeneratedRegex("^QS-[A-Z]{2,8}-[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public sealed class QualityRuleResolver
{
    private readonly QualityRuleLibrary library;

    public QualityRuleResolver(string? libraryRoot = null)
    {
        library = new QualityRuleLibrary(libraryRoot ?? LocateLibrary());
    }

    public ResolvedQualityRules Resolve(string repositoryRoot, IReadOnlyList<string> subjectPaths)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var normalizedPaths = subjectPaths.Select(NormalizePath).Distinct(StringComparer.Ordinal).ToArray();
        var configuration = LoadConfiguration(root);
        var knownRules = library.Load();
        var knownIds = knownRules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = configuration.Overrides.Select(value => value.RuleId)
            .FirstOrDefault(id => !knownIds.Contains(id));
        if (unknown is not null)
            throw new QualityRuleConfigurationException($"Rule override references unknown rule '{unknown}'.");

        var effective = new List<EffectiveQualityRule>();
        var canonical = new StringBuilder("quality-studio-effective-rules-v1\0");
        foreach (var rule in knownRules.Where(rule => normalizedPaths.Any(path => LanguageFor(path) == rule.Language)))
        {
            var pathStates = new List<PathRuleState>();
            foreach (var path in normalizedPaths.Where(path => LanguageFor(path) == rule.Language))
            {
                var enabled = rule.DefaultOn;
                var severity = rule.Severity;
                var autofixable = rule.Autofixable;
                var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var item in configuration.Overrides.Where(item => item.RuleId == rule.Id &&
                             ScopeMatches(item.Scope, rule.Language, path)))
                {
                    enabled = item.Enabled ?? enabled;
                    severity = item.Severity ?? severity;
                    autofixable = item.Autofixable ?? autofixable;
                    if (item.Parameters is not null)
                        foreach (var pair in item.Parameters) parameters[pair.Key] = pair.Value;
                }
                pathStates.Add(new PathRuleState(path, enabled, severity, autofixable, parameters));
                canonical.Append(rule.Id).Append('\0').Append(path).Append('\0').Append(enabled).Append('\0')
                    .Append(severity).Append('\0').Append(autofixable).Append('\0');
                foreach (var parameter in parameters.OrderBy(value => value.Key, StringComparer.Ordinal))
                    canonical.Append(parameter.Key).Append('=').Append(parameter.Value.GetRawText()).Append('\0');
            }
            var activeStates = pathStates.Where(state => state.Enabled).ToArray();
            if (activeStates.Length == 0) continue;
            var mergedParameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var state in activeStates)
                foreach (var parameter in state.Parameters) mergedParameters[parameter.Key] = parameter.Value;
            effective.Add(new EffectiveQualityRule(
                rule,
                activeStates.MinBy(state => (int)state.Severity)!.Severity,
                activeStates.All(state => state.Autofixable),
                mergedParameters));
        }

        var ordered = effective.OrderBy(rule => rule.Definition.Id, StringComparer.Ordinal).ToArray();
        foreach (var rule in ordered)
        {
            canonical.Append(rule.Definition.Id).Append('\0')
                .Append(rule.Definition.History[^1].Version).Append('\0')
                .Append(rule.Severity).Append('\0').Append(rule.Autofixable).Append('\0')
                .Append(rule.Definition.Statement).Append('\0').Append(rule.Definition.Rationale).Append('\0');
            foreach (var parameter in rule.Parameters.OrderBy(value => value.Key, StringComparer.Ordinal))
                canonical.Append(parameter.Key).Append('=').Append(parameter.Value.GetRawText()).Append('\0');
        }
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new ResolvedQualityRules(ordered, hash);
    }

    private static QualityRuleConfiguration LoadConfiguration(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot,
            QualityRuleConfiguration.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return new QualityRuleConfiguration();
        try
        {
            var configuration = JsonSerializer.Deserialize<QualityRuleConfiguration>(
                File.ReadAllText(path), QualityRuleLibrary.JsonOptions())
                ?? throw new QualityRuleConfigurationException($"Rule configuration '{path}' must be a JSON object.");
            if (configuration.Schema != QualityRuleConfiguration.SchemaId ||
                configuration.SchemaVersion != QualityRuleConfiguration.CurrentSchemaVersion)
                throw new QualityRuleConfigurationException($"Rule configuration '{path}' uses an unsupported schema.");
            if (configuration.Overrides.Any(item => string.IsNullOrWhiteSpace(item.RuleId)))
                throw new QualityRuleConfigurationException($"Rule configuration '{path}' contains an override without ruleId.");
            if (configuration.Overrides.Any(item => item.Scope?.Languages?.Any(language =>
                    language is not ("angular" or "csharp")) == true))
                throw new QualityRuleConfigurationException($"Rule configuration '{path}' contains an unsupported language.");
            if (configuration.Overrides.Any(item => item.Scope?.Include?.Any(string.IsNullOrWhiteSpace) == true ||
                                                        item.Scope?.Exclude?.Any(string.IsNullOrWhiteSpace) == true))
                throw new QualityRuleConfigurationException($"Rule configuration '{path}' contains an empty scope pattern.");
            return configuration;
        }
        catch (JsonException exception)
        {
            throw new QualityRuleConfigurationException($"Rule configuration '{path}' is invalid.", exception);
        }
    }

    private static bool ScopeMatches(QualityRuleScope? scope, string language, string path)
    {
        if (scope is null) return true;
        if (scope.Languages is { Count: > 0 } && !scope.Languages.Contains(language, StringComparer.OrdinalIgnoreCase))
            return false;
        if (scope.Include is { Count: > 0 } && !scope.Include.Any(pattern => Matches(pattern, path))) return false;
        return scope.Exclude is not { Count: > 0 } || !scope.Exclude.Any(pattern => Matches(pattern, path));
    }

    private static bool Matches(string pattern, string path) =>
        FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'), path, ignoreCase: false) ||
        (pattern.StartsWith("**/", StringComparison.Ordinal) &&
         FileSystemName.MatchesSimpleExpression(pattern.AsSpan(3), path, ignoreCase: false));

    internal static string? LanguageFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ts" or ".html" or ".css" or ".scss" => "angular",
        ".cs" => "csharp",
        _ => null,
    };

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }

    private static string LocateLibrary()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "rules");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("The bundled Quality Studio rules directory was not found.");
    }

    private sealed record PathRuleState(
        string Path,
        bool Enabled,
        FindingSeverity Severity,
        bool Autofixable,
        IReadOnlyDictionary<string, JsonElement> Parameters);
}

public sealed class QualityRuleFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class QualityRuleConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
