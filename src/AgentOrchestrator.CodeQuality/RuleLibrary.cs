using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRule(
    string Id,
    string Version,
    string Title,
    string Language,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> AppliesTo,
    FindingSeverity Severity,
    bool DefaultOn,
    bool Autofixable,
    bool Deterministic,
    string Statement,
    string Rationale,
    string BadExample,
    string GoodExample,
    string ChangeHistory,
    string Source);

public sealed record EffectiveQualityRule(
    QualityRule Rule,
    bool Enabled,
    FindingSeverity Severity,
    IReadOnlyDictionary<string, JsonElement> Options)
{
    public string PromptMarkdown() => $"""
        ## {Rule.Id} - {Rule.Title}

        Version: {Rule.Version}  
        Language: {Rule.Language}  
        Severity: {Severity.ToString().ToLowerInvariant()}  
        Autofixable: {Rule.Autofixable.ToString().ToLowerInvariant()}

        Statement: {Rule.Statement}

        Rationale: {Rule.Rationale}

        Bad example:

        {Rule.BadExample}

        Good example:

        {Rule.GoodExample}
        """ + (Options.Count == 0
            ? string.Empty
            : "\n\nProject options:\n\n```json\n" +
              JsonSerializer.Serialize(Options, new JsonSerializerOptions { WriteIndented = true }) + "\n```");
}

public sealed class RuleLibrary
{
    private const string ResourcePrefix = "QualityStudio.Rules/";
    private static readonly Regex IdPattern = new("^QS-[A-Z]{2,8}-[0-9]{3}$", RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<QualityRule> rules;

    public RuleLibrary(IEnumerable<QualityRule> rules)
    {
        this.rules = rules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
        var duplicate = this.rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new RuleFormatException($"Rule id '{duplicate.Key}' is duplicated.");
    }

    public static RuleLibrary BuiltIn { get; } = LoadEmbedded();

    public IReadOnlyList<QualityRule> List() => rules;

    public IReadOnlyList<EffectiveQualityRule> Resolve(
        string repositoryRoot,
        IReadOnlyList<string> subjectPaths,
        string kind)
    {
        var configuration = RuleConfiguration.Load(repositoryRoot, rules);
        return Resolve(configuration, subjectPaths, kind);
    }

    internal IReadOnlyList<EffectiveQualityRule> Resolve(
        RuleConfiguration configuration,
        IReadOnlyList<string> subjectPaths,
        string kind)
    {
        var normalizedPaths = subjectPaths.Select(NormalizePath).Distinct(StringComparer.Ordinal).ToArray();
        return rules
            .Where(rule => rule.Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ||
                           rule.Kinds.Contains("all", StringComparer.OrdinalIgnoreCase))
            .Where(rule => normalizedPaths.Length == 0 || normalizedPaths.Any(path => Applies(rule, path)))
            .Select(rule => configuration.Apply(rule, normalizedPaths))
            .Where(rule => rule.Enabled)
            .OrderBy(rule => rule.Rule.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static RuleLibrary LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Rule directory does not exist: {root}");
        return new RuleLibrary(Directory.EnumerateFiles(root, "*.md", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        }).Order(StringComparer.Ordinal).Select(path => Parse(File.ReadAllText(path), Path.GetRelativePath(root, path).Replace('\\', '/'))));
    }

    public static QualityRule Parse(string markdown, string source = "memory")
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new RuleFormatException($"Rule '{source}' must start with frontmatter.");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new RuleFormatException($"Rule '{source}' has unterminated frontmatter.");
        var fields = ParseFields(text[4..end], source);
        var id = Required(fields, "id", source);
        if (!IdPattern.IsMatch(id))
            throw new RuleFormatException($"Rule '{source}' id must match QS-<LANGUAGE>-<NUMBER>.");
        var severityText = Required(fields, "severity", source);
        if (!TryParseSeverity(severityText, out var severity))
            throw new RuleFormatException($"Rule '{source}' has unsupported severity '{severityText}'.");
        var body = text[(end + 5)..].Trim();
        var sections = ParseSections(body, source);
        return new QualityRule(
            id,
            Required(fields, "version", source),
            Required(fields, "title", source),
            Required(fields, "language", source).ToLowerInvariant(),
            Values(fields, "kinds", source),
            Values(fields, "appliesTo", source),
            severity,
            Boolean(fields, "defaultOn", source),
            Boolean(fields, "autofixable", source),
            Boolean(fields, "deterministic", source),
            Section(sections, "Statement", source),
            Section(sections, "Rationale", source),
            Section(sections, "Bad example", source),
            Section(sections, "Good example", source),
            Section(sections, "Change history", source),
            source);
    }

    private static RuleLibrary LoadEmbedded()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                           name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) throw new RuleFormatException("The built-in rule library is empty.");
        var loaded = new List<QualityRule>(resources.Length);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            loaded.Add(Parse(reader.ReadToEnd(), resource[ResourcePrefix.Length..]));
        }
        return new RuleLibrary(loaded);
    }

    private static bool Applies(QualityRule rule, string path) =>
        rule.AppliesTo.Any(pattern => RepositoryScope.PatternMatches(pattern, path));

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    private static bool TryParseSeverity(string value, out FindingSeverity severity)
    {
        severity = default;
        return string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) &&
               !int.TryParse(value, out _) && Enum.TryParse(value, true, out severity) && Enum.IsDefined(severity);
    }

    private static Dictionary<string, string> ParseFields(string frontmatter, string source)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) throw new RuleFormatException($"Rule '{source}' has invalid frontmatter line '{line}'.");
            var name = line[..separator].Trim();
            if (!fields.TryAdd(name, line[(separator + 1)..].Trim()))
                throw new RuleFormatException($"Rule '{source}' repeats frontmatter field '{name}'.");
        }
        var allowed = new[] { "id", "version", "title", "language", "kinds", "appliesTo", "severity", "defaultOn", "autofixable", "deterministic" };
        var unsupported = fields.Keys.FirstOrDefault(name => !allowed.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (unsupported is not null) throw new RuleFormatException($"Rule '{source}' has unsupported frontmatter field '{unsupported}'.");
        return fields;
    }

    private static Dictionary<string, string> ParseSections(string body, string source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var content = new StringBuilder();
        void Store()
        {
            if (current is null) return;
            if (!result.TryAdd(current, content.ToString().Trim()))
                throw new RuleFormatException($"Rule '{source}' repeats section '{current}'.");
            content.Clear();
        }
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Store();
                current = line[3..].Trim();
            }
            else if (current is not null)
            {
                content.AppendLine(line);
            }
        }
        Store();
        return result;
    }

    private static string Required(Dictionary<string, string> fields, string name, string source) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().Trim('"')
            : throw new RuleFormatException($"Rule '{source}' requires '{name}'.");

    private static bool Boolean(Dictionary<string, string> fields, string name, string source) =>
        bool.TryParse(Required(fields, name, source), out var value)
            ? value
            : throw new RuleFormatException($"Rule '{source}' field '{name}' must be true or false.");

    private static IReadOnlyList<string> Values(Dictionary<string, string> fields, string name, string source)
    {
        var values = Required(fields, name, source).TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('"', '\''))
            .Where(value => value.Length > 0)
            .ToArray();
        return values.Length > 0 ? values : throw new RuleFormatException($"Rule '{source}' field '{name}' cannot be empty.");
    }

    private static string Section(Dictionary<string, string> sections, string name, string source) =>
        sections.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new RuleFormatException($"Rule '{source}' requires section '{name}'.");
}

public sealed record RuleOverride(
    bool? Enabled,
    FindingSeverity? Severity,
    IReadOnlyDictionary<string, JsonElement> Options);

public sealed record ScopedRuleOverrides(
    IReadOnlyList<string> Paths,
    IReadOnlyDictionary<string, RuleOverride> Rules);

public sealed class RuleConfiguration
{
    public const string RelativePath = ".quality/rules.json";
    public const string Schema = "https://agent-orchestrator.dev/quality/schemas/rule-config.v1.schema.json";
    private readonly IReadOnlyDictionary<string, RuleOverride> rules;
    private readonly IReadOnlyList<ScopedRuleOverrides> scopes;

    private RuleConfiguration(
        IReadOnlyDictionary<string, RuleOverride> rules,
        IReadOnlyList<ScopedRuleOverrides> scopes)
    {
        this.rules = rules;
        this.scopes = scopes;
    }

    public static RuleConfiguration Load(string repositoryRoot, IReadOnlyList<QualityRule>? library = null)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return new RuleConfiguration(new Dictionary<string, RuleOverride>(StringComparer.Ordinal), []);
        try
        {
            return Parse(File.ReadAllText(path), library);
        }
        catch (JsonException exception)
        {
            throw new RuleConfigurationException($"{RelativePath} is invalid JSON: {exception.Message}", exception);
        }
    }

    private static RuleConfiguration Parse(string json, IReadOnlyList<QualityRule>? library)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        RequireObject(root, RelativePath);
        RejectProperties(root, ["$schema", "version", "rules", "scopes"], RelativePath);
        if (!root.TryGetProperty("$schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema)
            throw new RuleConfigurationException($"{RelativePath} requires the supported '$schema'.");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionNumber) || versionNumber != 1)
            throw new RuleConfigurationException($"{RelativePath} requires version 1.");
        var known = library?.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        var overrides = root.TryGetProperty("rules", out var configuredRules)
            ? ParseOverrides(configuredRules, "rules", known)
            : new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        var scoped = new List<ScopedRuleOverrides>();
        if (root.TryGetProperty("scopes", out var configuredScopes))
        {
            if (configuredScopes.ValueKind != JsonValueKind.Array)
                throw new RuleConfigurationException($"{RelativePath} 'scopes' must be an array.");
            var index = 0;
            foreach (var scope in configuredScopes.EnumerateArray())
            {
                index++;
                RequireObject(scope, $"scope {index}");
                RejectProperties(scope, ["paths", "rules"], $"scope {index}");
                if (!scope.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
                    throw new RuleConfigurationException($"{RelativePath} scope {index} requires a 'paths' array.");
                var patterns = paths.EnumerateArray().Select(pathElement =>
                {
                    if (pathElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathElement.GetString()))
                        throw new RuleConfigurationException($"{RelativePath} scope {index} paths must be non-empty strings.");
                    var pattern = pathElement.GetString()!.Replace('\\', '/').TrimStart('/');
                    if (pattern.Split('/').Contains("..", StringComparer.Ordinal))
                        throw new RuleConfigurationException($"{RelativePath} scope paths cannot contain parent traversal.");
                    return pattern;
                }).Distinct(StringComparer.Ordinal).ToArray();
                if (patterns.Length == 0) throw new RuleConfigurationException($"{RelativePath} scope {index} requires at least one path.");
                if (!scope.TryGetProperty("rules", out var scopeRules))
                    throw new RuleConfigurationException($"{RelativePath} scope {index} requires 'rules'.");
                scoped.Add(new ScopedRuleOverrides(patterns, ParseOverrides(scopeRules, $"scope {index} rules", known)));
            }
        }
        return new RuleConfiguration(overrides, scoped);
    }

    public EffectiveQualityRule Apply(QualityRule rule, IReadOnlyList<string> subjectPaths)
    {
        var enabled = rule.DefaultOn;
        var severity = rule.Severity;
        var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Apply(rules.GetValueOrDefault(rule.Id));
        foreach (var scope in scopes.Where(scope => subjectPaths.Any(path =>
                     scope.Paths.Any(pattern => RepositoryScope.PatternMatches(pattern, path)))))
            Apply(scope.Rules.GetValueOrDefault(rule.Id));
        return new EffectiveQualityRule(rule, enabled, severity, options);

        void Apply(RuleOverride? value)
        {
            if (value is null) return;
            enabled = value.Enabled ?? enabled;
            severity = value.Severity ?? severity;
            foreach (var option in value.Options) options[option.Key] = option.Value;
        }
    }

    private static Dictionary<string, RuleOverride> ParseOverrides(
        JsonElement element,
        string context,
        IReadOnlySet<string>? known)
    {
        RequireObject(element, context);
        var result = new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (known is not null && !known.Contains(property.Name))
                throw new RuleConfigurationException($"{RelativePath} references unknown rule '{property.Name}'.");
            RequireObject(property.Value, $"override {property.Name}");
            RejectProperties(property.Value, ["enabled", "severity", "options"], $"override {property.Name}");
            bool? enabled = null;
            if (property.Value.TryGetProperty("enabled", out var enabledElement))
            {
                if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new RuleConfigurationException($"{RelativePath} override '{property.Name}' enabled must be boolean.");
                enabled = enabledElement.GetBoolean();
            }
            FindingSeverity? severity = null;
            if (property.Value.TryGetProperty("severity", out var severityElement))
            {
                if (severityElement.ValueKind != JsonValueKind.String ||
                    !TryParseSeverity(severityElement.GetString()!, out var parsed))
                    throw new RuleConfigurationException($"{RelativePath} override '{property.Name}' has invalid severity.");
                severity = parsed;
            }
            var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (property.Value.TryGetProperty("options", out var optionsElement))
            {
                RequireObject(optionsElement, $"override {property.Name} options");
                foreach (var option in optionsElement.EnumerateObject()) options.Add(option.Name, option.Value.Clone());
            }
            if (enabled is null && severity is null && options.Count == 0)
                throw new RuleConfigurationException($"{RelativePath} override '{property.Name}' cannot be empty.");
            result.Add(property.Name, new RuleOverride(enabled, severity, options));
        }
        return result;
    }

    private static void RequireObject(JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new RuleConfigurationException($"{RelativePath} {context} must be an object.");
    }

    private static bool TryParseSeverity(string value, out FindingSeverity severity)
    {
        severity = default;
        return string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal) &&
               !int.TryParse(value, out _) && Enum.TryParse(value, true, out severity) && Enum.IsDefined(severity);
    }

    private static void RejectProperties(JsonElement element, IReadOnlyList<string> allowed, string context)
    {
        var unsupported = element.EnumerateObject().Select(property => property.Name)
            .FirstOrDefault(name => !allowed.Contains(name, StringComparer.Ordinal));
        if (unsupported is not null)
            throw new RuleConfigurationException($"{RelativePath} {context} contains unsupported property '{unsupported}'.");
    }
}

public sealed class RuleFormatException(string message) : Exception(message);
public sealed class RuleConfigurationException(string message, Exception? innerException = null) : Exception(message, innerException);
