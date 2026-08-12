using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRule(
    string Id,
    string Title,
    string Language,
    FindingSeverity Severity,
    bool Autofixable,
    bool DefaultEnabled,
    string Version,
    string Status,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Levels,
    IReadOnlyList<string> AppliesTo,
    string Statement,
    string Rationale,
    string BadExample,
    string GoodExample,
    string ChangeHistory,
    IReadOnlyList<string> References,
    string? DeterministicCheck,
    string Source)
{
    public bool Applies(string kind, ReviewLevel level, IReadOnlyList<string> subjectPaths)
    {
        if (!string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase) ||
            !Kinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ||
            !Levels.Contains(level.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return subjectPaths.Any(path =>
            AppliesTo.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    public string PromptContent() =>
        $"### [{Id}] {Title}\n" +
        $"Rule version: {Version}\n" +
        $"Severity: {Severity.ToString().ToLowerInvariant()}\n" +
        $"Autofixable: {Autofixable.ToString().ToLowerInvariant()}\n\n" +
        $"Statement: {Statement}\n\n" +
        $"Rationale: {Rationale}\n\n" +
        $"Bad example:\n{BadExample}\n\n" +
        $"Good example:\n{GoodExample}";
}

/// <summary>
/// Loads the versioned, file-first Quality Studio rule catalogue. The checked-in
/// Markdown files are embedded in the core package so registered repositories use
/// the same rules even when they do not contain Quality Studio's rules directory.
/// </summary>
public sealed class RuleLibrary
{
    private const string RuleResourceMarker = ".rules.";
    private static readonly Regex StableId = new(
        "^QS-[A-Z][A-Z0-9]{1,7}-[0-9]{3}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SemanticVersion = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedKinds =
        ["code", "security", "performance"];
    private static readonly HashSet<string> SupportedSeverities =
        Enum.GetNames<FindingSeverity>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RequiredSectionOrder =
        ["Statement", "Rationale", "Bad example", "Good example", "Change history"];
    private static readonly HashSet<string> SupportedLevels = Enum.GetNames<ReviewLevel>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<IReadOnlyList<QualityRule>> EmbeddedRules =
        new(LoadEmbeddedRules, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly IReadOnlyList<QualityRule> rules;

    public RuleLibrary() : this(EmbeddedRules.Value)
    {
    }

    internal RuleLibrary(IReadOnlyList<QualityRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var duplicate = rules.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new RuleFormatException($"Rule id '{duplicate.Key}' is duplicated.");
        }

        this.rules = rules.OrderBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<QualityRule> List() => rules;

    public IReadOnlyList<QualityRule> Resolve(
        IReadOnlyList<string> subjectPaths,
        string kind,
        ReviewLevel level,
        QualityRuleConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(subjectPaths);
        if (string.IsNullOrWhiteSpace(kind) || !SupportedKinds.Contains(kind.ToLowerInvariant()))
        {
            throw new ArgumentException($"Unsupported review kind: {kind}", nameof(kind));
        }

        configuration ??= QualityRuleConfiguration.Empty;
        return rules.Where(rule => configuration.IsEnabled(rule) && rule.Applies(kind, level, subjectPaths))
            .Select(configuration.Apply)
            .OrderBy(rule => rule.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static RuleLibrary LoadDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A rules directory is required.", nameof(directory));
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Rules directory does not exist: {root}");
        }

        var loaded = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                var source = Path.GetRelativePath(root, path).Replace('\\', '/');
                var rule = Parse(File.ReadAllText(path), source);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), rule.Id, StringComparison.Ordinal))
                {
                    throw new RuleFormatException(
                        $"Rule '{source}' file name must match its stable id '{rule.Id}'.");
                }
                return rule;
            })
            .ToArray();
        return new RuleLibrary(loaded);
    }

    public static QualityRule Parse(string markdown, string source)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new RuleFormatException($"Rule '{source}' is empty.");
        }

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new RuleFormatException($"Rule '{source}' must start with YAML frontmatter.");
        }

        var frontmatterEnd = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (frontmatterEnd < 0)
        {
            throw new RuleFormatException($"Rule '{source}' has unterminated frontmatter.");
        }

        var fields = ParseFields(normalized[4..frontmatterEnd], source);
        var body = normalized[(frontmatterEnd + 5)..].Trim();
        var sections = ParseSections(body, source);
        var id = Required(fields, "id", source);
        if (!StableId.IsMatch(id))
        {
            throw new RuleFormatException(
                $"Rule '{source}' id must match QS-<LANGUAGE>-<three digits>.");
        }

        var version = Required(fields, "version", source);
        if (!SemanticVersion.IsMatch(version))
        {
            throw new RuleFormatException($"Rule '{source}' requires a semantic version such as 1.0.0.");
        }

        var severityText = Required(fields, "severity", source);
        if (!SupportedSeverities.Contains(severityText) ||
            !Enum.TryParse<FindingSeverity>(severityText, true, out var severity))
        {
            throw new RuleFormatException($"Rule '{source}' has unsupported severity '{severityText}'.");
        }

        var autofixText = Required(fields, "autofixable", source);
        if (!bool.TryParse(autofixText, out var autofixable))
        {
            throw new RuleFormatException($"Rule '{source}' requires autofixable to be true or false.");
        }

        var defaultEnabledText = Required(fields, "default-enabled", source);
        if (!bool.TryParse(defaultEnabledText, out var defaultEnabled))
        {
            throw new RuleFormatException($"Rule '{source}' requires default-enabled to be true or false.");
        }

        var kinds = Values(fields, "kinds", source);
        if (kinds.Count == 0 || kinds.Any(kind => !SupportedKinds.Contains(kind)))
        {
            throw new RuleFormatException($"Rule '{source}' requires supported review kinds.");
        }

        var levels = Values(fields, "levels", source);
        if (levels.Count == 0 || levels.Any(level => !SupportedLevels.Contains(level)))
        {
            throw new RuleFormatException($"Rule '{source}' requires supported review levels.");
        }

        var appliesTo = Values(fields, "applies-to", source);
        if (appliesTo.Count == 0 || appliesTo.Any(extension =>
                extension.Length < 2 || extension[0] != '.' || extension.Any(char.IsWhiteSpace)))
        {
            throw new RuleFormatException($"Rule '{source}' requires file extensions in applies-to.");
        }

        var status = Required(fields, "status", source).ToLowerInvariant();
        if (status is not ("active" or "deprecated"))
        {
            throw new RuleFormatException($"Rule '{source}' status must be active or deprecated.");
        }

        var deterministicCheck = Required(fields, "deterministic-check", source);
        if (string.Equals(deterministicCheck, "none", StringComparison.OrdinalIgnoreCase))
        {
            deterministicCheck = null;
        }

        var changeHistory = RequiredSection(sections, "Change history", source);
        if (!changeHistory.Contains(version, StringComparison.Ordinal))
        {
            throw new RuleFormatException(
                $"Rule '{source}' change history must contain current version '{version}'.");
        }

        return new QualityRule(
            id,
            Required(fields, "title", source),
            Required(fields, "language", source).ToLowerInvariant(),
            severity,
            autofixable,
            defaultEnabled,
            version,
            status,
            kinds.Select(kind => kind.ToLowerInvariant()).ToArray(),
            levels.Select(level => level.ToLowerInvariant()).ToArray(),
            appliesTo.Select(extension => extension.ToLowerInvariant()).ToArray(),
            RequiredSection(sections, "Statement", source),
            RequiredSection(sections, "Rationale", source),
            RequiredSection(sections, "Bad example", source),
            RequiredSection(sections, "Good example", source),
            changeHistory,
            Values(fields, "references", source),
            deterministicCheck,
            source);
    }

    private static IReadOnlyList<QualityRule> LoadEmbeddedRules()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(RuleResourceMarker, StringComparison.Ordinal) &&
                           name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
                           !name.EndsWith(".README.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd(), "embedded:" + name);
            })
            .ToArray();
    }

    private static Dictionary<string, string> ParseFields(string frontmatter, string source)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in frontmatter.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new RuleFormatException($"Rule '{source}' has invalid frontmatter line '{line}'.");
            }

            var name = line[..separator].Trim();
            if (!fields.TryAdd(name, line[(separator + 1)..].Trim()))
            {
                throw new RuleFormatException($"Rule '{source}' repeats frontmatter field '{name}'.");
            }
        }

        return fields;
    }

    private static Dictionary<string, string> ParseSections(string body, string source)
    {
        var headings = Regex.Matches(body, "^## (?<name>[^\\n]+)$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var name = heading.Groups["name"].Value.Trim();
            var start = heading.Index + heading.Length;
            var end = index + 1 < headings.Count ? headings[index + 1].Index : body.Length;
            if (!sections.TryAdd(name, body[start..end].Trim()))
            {
                throw new RuleFormatException($"Rule '{source}' repeats section '{name}'.");
            }
        }
        var actualRequiredOrder = headings.Select(heading => heading.Groups["name"].Value.Trim())
            .Where(RequiredSectionOrder.Contains)
            .ToArray();
        if (!actualRequiredOrder.SequenceEqual(RequiredSectionOrder, StringComparer.Ordinal))
            throw new RuleFormatException($"Rule '{source}' requires Statement, Rationale, Bad example, Good example, and Change history sections in that order.");

        return sections;
    }

    private static string Required(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string source) =>
        fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new RuleFormatException($"Rule '{source}' requires frontmatter field '{name}'.");

    private static string RequiredSection(
        IReadOnlyDictionary<string, string> sections,
        string name,
        string source) =>
        sections.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new RuleFormatException($"Rule '{source}' requires section '{name}'.");

    private static IReadOnlyList<string> Values(
        IReadOnlyDictionary<string, string> fields,
        string name,
        string source,
        bool required = true)
    {
        if (!fields.TryGetValue(name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return required
                ? throw new RuleFormatException($"Rule '{source}' requires frontmatter field '{name}'.")
                : [];
        }

        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) ||
            !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            throw new RuleFormatException($"Rule '{source}' field '{name}' must be a bracketed list.");
        }

        return trimmed[1..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Trim('"', '\''))
            .Where(value => value.Length > 0)
            .ToArray();
    }
}

public sealed class RuleFormatException(string message) : Exception(message);

public sealed record QualityRuleOverride(bool? Enabled, FindingSeverity? Severity);

/// <summary>
/// Reads repository-owned overrides for the built-in rule catalogue. Version 1
/// deliberately has repository scope only: nested configuration is ignored and
/// every override names one exact, existing rule id.
/// </summary>
public sealed class QualityRuleConfiguration
{
    public const string RelativePath = ".quality/rules.json";
    public const string SchemaId = "https://quality.studio/schemas/rule-config.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
    private static readonly HashSet<string> RootProperties =
        ["$schema", "schemaVersion", "rules"];
    private static readonly HashSet<string> OverrideProperties =
        ["enabled", "severity"];
    private readonly IReadOnlyDictionary<string, QualityRuleOverride> overrides;

    private QualityRuleConfiguration(IReadOnlyDictionary<string, QualityRuleOverride> overrides) =>
        this.overrides = overrides;

    public static QualityRuleConfiguration Empty { get; } =
        new(new Dictionary<string, QualityRuleOverride>(StringComparer.Ordinal));

    public static QualityRuleConfiguration Load(string repositoryRoot, RuleLibrary library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(library);
        var root = Path.GetFullPath(repositoryRoot);
        var qualityDirectory = Path.Combine(root, ".quality");
        var path = Path.Combine(qualityDirectory, "rules.json");
        if (!File.Exists(path)) return Empty;
        if (Directory.Exists(qualityDirectory) &&
            File.GetAttributes(qualityDirectory).HasFlag(FileAttributes.ReparsePoint))
            throw new RuleConfigurationException($"Rule configuration directory '.quality' cannot be a symbolic link or junction.");
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new RuleConfigurationException($"Rule configuration '{RelativePath}' cannot be a symbolic link or junction.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException exception)
        {
            throw new RuleConfigurationException($"Rule configuration '{RelativePath}' is invalid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object)
                throw new RuleConfigurationException($"Rule configuration '{RelativePath}' must be a JSON object.");
            RejectUnknownProperties(rootElement, RootProperties, RelativePath);
            if (!rootElement.TryGetProperty("$schema", out var schema) ||
                schema.ValueKind != JsonValueKind.String || schema.GetString() != SchemaId)
                throw new RuleConfigurationException($"Rule configuration '{RelativePath}' requires $schema '{SchemaId}'.");
            if (!rootElement.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var parsedVersion) ||
                parsedVersion != CurrentSchemaVersion)
                throw new RuleConfigurationException($"Rule configuration '{RelativePath}' requires schemaVersion {CurrentSchemaVersion}.");
            if (!rootElement.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Object)
                throw new RuleConfigurationException($"Rule configuration '{RelativePath}' requires a rules object.");

            var knownIds = library.List().Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            var configured = new Dictionary<string, QualityRuleOverride>(StringComparer.Ordinal);
            foreach (var property in rulesElement.EnumerateObject())
            {
                if (!knownIds.Contains(property.Name))
                    throw new RuleConfigurationException($"Rule configuration '{RelativePath}' names unknown rule id '{property.Name}'.");
                if (!configured.TryAdd(property.Name, ParseOverride(property.Name, property.Value)))
                    throw new RuleConfigurationException($"Rule configuration '{RelativePath}' repeats rule id '{property.Name}'.");
            }
            return new QualityRuleConfiguration(configured);
        }
    }

    public bool IsEnabled(QualityRule rule) =>
        overrides.TryGetValue(rule.Id, out var value) && value.Enabled.HasValue
            ? value.Enabled.Value
            : rule.DefaultEnabled;

    public QualityRule Apply(QualityRule rule) =>
        overrides.TryGetValue(rule.Id, out var value) && value.Severity.HasValue
            ? rule with { Severity = value.Severity.Value }
            : rule;

    private static QualityRuleOverride ParseOverride(string id, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new RuleConfigurationException($"Override for '{id}' must be a JSON object.");
        RejectUnknownProperties(element, OverrideProperties, id);
        bool? enabled = null;
        FindingSeverity? severity = null;
        if (element.TryGetProperty("enabled", out var enabledElement))
        {
            if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new RuleConfigurationException($"Override '{id}.enabled' must be true or false.");
            enabled = enabledElement.GetBoolean();
        }
        if (element.TryGetProperty("severity", out var severityElement))
        {
            var parsedSeverity = severityElement.ValueKind == JsonValueKind.String
                ? severityElement.GetString() switch
                {
                    "critical" => FindingSeverity.Critical,
                    "high" => FindingSeverity.High,
                    "medium" => FindingSeverity.Medium,
                    "low" => FindingSeverity.Low,
                    "info" => FindingSeverity.Info,
                    _ => (FindingSeverity?)null,
                }
                : null;
            if (!parsedSeverity.HasValue)
                throw new RuleConfigurationException($"Override '{id}.severity' is unsupported.");
            severity = parsedSeverity.Value;
        }
        if (!enabled.HasValue && !severity.HasValue)
            throw new RuleConfigurationException($"Override for '{id}' must set enabled or severity.");
        return new QualityRuleOverride(enabled, severity);
    }

    private static void RejectUnknownProperties(JsonElement element, IReadOnlySet<string> allowed, string location)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new RuleConfigurationException($"Rule configuration '{location}' repeats property '{property.Name}'.");
            if (!allowed.Contains(property.Name))
                throw new RuleConfigurationException($"Rule configuration '{location}' contains unsupported property '{property.Name}'.");
        }
    }
}

public sealed class RuleConfigurationException : Exception
{
    public RuleConfigurationException(string message) : base(message) { }
    public RuleConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
