using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleApplicability(
    IReadOnlyList<string> ReviewKinds,
    IReadOnlyList<string> FileExtensions);

public sealed record RuleExample(string Language, string Code);

public sealed record RuleHistoryEntry(string Date, string Change);

public sealed record RuleDeterministicCheck(string Id);

public sealed record QualityRule
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-rule.v1.schema.json";

    [JsonPropertyName("$schema")]
    public required string Schema { get; init; }

    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Name { get; init; }
    public required string Language { get; init; }
    public required string Category { get; init; }
    public required string Statement { get; init; }
    public required string Rationale { get; init; }
    public required RuleExample BadExample { get; init; }
    public required RuleExample GoodExample { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required bool Autofixable { get; init; }
    public required bool DefaultOn { get; init; }
    public required RuleApplicability AppliesTo { get; init; }
    public RuleDeterministicCheck? DeterministicCheck { get; init; }
    public required IReadOnlyList<RuleHistoryEntry> History { get; init; }
}

public sealed record RuleOverride(bool? Enabled = null, FindingSeverity? Severity = null);

public sealed record ResolvedRule(
    QualityRule Definition,
    FindingSeverity Severity,
    bool Overridden,
    string ContentHash);

public sealed record ResolvedRuleSet(
    IReadOnlyList<ResolvedRule> Rules,
    string? ConfigurationPath,
    string EffectiveHash)
{
    public string PromptContext()
    {
        if (Rules.Count == 0) return "No named Quality Studio rules apply to this subject.";
        var builder = new StringBuilder();
        builder.AppendLine("Treat these named rules as authoritative review context. When a finding is a violation of one of them, use its exact `QS-*` id as `ruleId`. Do not cite a disabled or unrelated named rule.");
        foreach (var rule in Rules)
        {
            builder.AppendLine();
            builder.Append("### ").Append(rule.Definition.Id).Append(" — ").AppendLine(rule.Definition.Name);
            builder.Append("- Severity: ").Append(rule.Severity.ToString().ToLowerInvariant()).AppendLine();
            builder.Append("- Autofixable: ").Append(rule.Definition.Autofixable ? "yes" : "no").AppendLine();
            builder.Append("- Statement: ").AppendLine(rule.Definition.Statement);
            builder.Append("- Rationale: ").AppendLine(rule.Definition.Rationale);
            builder.AppendLine("- Bad example:");
            builder.Append("```").AppendLine(rule.Definition.BadExample.Language);
            builder.AppendLine(rule.Definition.BadExample.Code.TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine("- Good example:");
            builder.Append("```").AppendLine(rule.Definition.GoodExample.Language);
            builder.AppendLine(rule.Definition.GoodExample.Code.TrimEnd());
            builder.AppendLine("```");
        }
        return builder.ToString().TrimEnd();
    }
}

public sealed class RuleLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly IReadOnlyList<RuleSource> sources;

    public RuleLibrary() : this(LoadEmbedded())
    {
    }

    internal RuleLibrary(IReadOnlyList<RuleSource> sources)
    {
        this.sources = sources;
        var duplicate = sources.GroupBy(source => source.Rule.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Duplicate Quality Studio rule id '{duplicate.Key}'.");
    }

    public IReadOnlyList<QualityRule> Rules => sources.Select(source => source.Rule).ToArray();

    public ResolvedRuleSet Resolve(
        string repositoryRoot,
        string kind,
        IReadOnlyList<string> subjectPaths)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var overrides = RuleConfiguration.Load(root, sources.Select(source => source.Rule.Id));
        var rules = new List<ResolvedRule>();
        foreach (var source in sources.OrderBy(source => source.Rule.Id, StringComparer.Ordinal))
        {
            var rule = source.Rule;
            if (!rule.AppliesTo.ReviewKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) ||
                !subjectPaths.Any(path => AppliesToPath(root, path, rule))) continue;
            overrides.Values.TryGetValue(rule.Id, out var configured);
            if (!(configured?.Enabled ?? rule.DefaultOn)) continue;
            rules.Add(new ResolvedRule(rule, configured?.Severity ?? rule.Severity,
                configured is not null, source.ContentHash));
        }

        var canonical = new StringBuilder("quality-studio-named-rules-v1\0");
        foreach (var rule in rules)
        {
            canonical.Append(rule.Definition.Id).Append('\0')
                .Append(rule.Definition.Version).Append('\0')
                .Append(rule.ContentHash).Append('\0')
                .Append(rule.Severity.ToString().ToLowerInvariant()).Append('\0');
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new ResolvedRuleSet(rules, overrides.ConfigurationPath, hash);
    }

    private static bool AppliesToPath(string root, string path, QualityRule rule)
    {
        var extension = Path.GetExtension(path);
        if (!rule.AppliesTo.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        if (!string.Equals(rule.Language, "angular", StringComparison.OrdinalIgnoreCase)) return true;

        var absolute = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var directory = Directory.Exists(absolute) ? absolute : Path.GetDirectoryName(absolute);
        while (directory is not null && IsWithin(root, directory))
        {
            if (File.Exists(Path.Combine(directory, "angular.json"))) return true;
            if (string.Equals(directory, root, PathComparison)) break;
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    private static bool IsWithin(string root, string path) =>
        string.Equals(root, path, PathComparison) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static IReadOnlyList<RuleSource> LoadEmbedded()
    {
        var assembly = typeof(RuleLibrary).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".rules.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) throw new InvalidDataException("The embedded Quality Studio rule library is empty.");
        var result = new List<RuleSource>(resources.Length);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var rule = JsonSerializer.Deserialize<QualityRule>(json, JsonOptions)
                ?? throw new InvalidDataException($"Rule resource '{resource}' is empty.");
            Validate(rule, resource);
            var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            result.Add(new RuleSource(rule, hash));
        }
        return result;
    }

    private static void Validate(QualityRule rule, string source)
    {
        if (rule.SchemaVersion != QualityRule.CurrentSchemaVersion ||
            !string.Equals(rule.Schema, QualityRule.SchemaId, StringComparison.Ordinal))
            throw new InvalidDataException($"Rule '{source}' has an unsupported schema.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(rule.Id, "^QS-[A-Z]{2}-[0-9]{3}$"))
            throw new InvalidDataException($"Rule '{source}' has invalid id '{rule.Id}'.");
        if (string.IsNullOrWhiteSpace(rule.Version) || string.IsNullOrWhiteSpace(rule.Name) ||
            string.IsNullOrWhiteSpace(rule.Statement) || string.IsNullOrWhiteSpace(rule.Rationale) ||
            string.IsNullOrWhiteSpace(rule.BadExample.Code) || string.IsNullOrWhiteSpace(rule.GoodExample.Code) ||
            rule.AppliesTo.ReviewKinds.Count == 0 || rule.AppliesTo.FileExtensions.Count == 0 || rule.History.Count == 0)
            throw new InvalidDataException($"Rule '{rule.Id}' is missing required content.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    internal sealed record RuleSource(QualityRule Rule, string ContentHash);
}

internal sealed record RuleConfiguration(
    string? ConfigurationPath,
    IReadOnlyDictionary<string, RuleOverride> Values)
{
    internal const string RelativePath = ".quality/rules.json";

    internal static RuleConfiguration Load(string repositoryRoot, IEnumerable<string> knownRuleIds)
    {
        var path = Path.Combine(repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return new RuleConfiguration(null, new Dictionary<string, RuleOverride>(StringComparer.Ordinal));
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Any(property => property.Name is not ("$schema" or "schemaVersion" or "rules")) ||
            !root.TryGetProperty("$schema", out var schema) || schema.ValueKind != JsonValueKind.String ||
            !string.Equals(schema.GetString(), "https://quality.studio/schemas/rule-configuration.v1.schema.json", StringComparison.Ordinal) ||
            !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
            !root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{RelativePath} must be a v1 object with a 'rules' object.");

        var known = knownRuleIds.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        foreach (var property in rules.EnumerateObject())
        {
            if (!known.Contains(property.Name))
                throw new InvalidDataException($"{RelativePath} references unknown rule '{property.Name}'.");
            if (property.Value.ValueKind != JsonValueKind.Object ||
                property.Value.EnumerateObject().Any(value => value.Name is not ("enabled" or "severity")))
                throw new InvalidDataException($"{RelativePath} override '{property.Name}' contains unsupported properties.");
            bool? enabled = null;
            FindingSeverity? severity = null;
            if (property.Value.TryGetProperty("enabled", out var enabledValue))
            {
                if (enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException($"{RelativePath} override '{property.Name}'.enabled must be boolean.");
                enabled = enabledValue.GetBoolean();
            }
            if (property.Value.TryGetProperty("severity", out var severityValue))
            {
                if (severityValue.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<FindingSeverity>(severityValue.GetString(), true, out var parsed))
                    throw new InvalidDataException($"{RelativePath} override '{property.Name}'.severity is invalid.");
                severity = parsed;
            }
            if (enabled is null && severity is null)
                throw new InvalidDataException($"{RelativePath} override '{property.Name}' must set enabled or severity.");
            result[property.Name] = new RuleOverride(enabled, severity);
        }
        return new RuleConfiguration(RelativePath, result);
    }
}
