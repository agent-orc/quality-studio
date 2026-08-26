using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleOverride(bool? Enabled = null, string? Severity = null, string? Reason = null);

/// <summary>
/// Deviations from the rule library's shipped defaultOn set for one project, read from
/// &lt;repository&gt;/.quality/rules.config.json (schemas/rule-config.v1.schema.json). The default set
/// itself is not repeated in this file; absence of the file means every defaultOn rule applies unmodified.
/// </summary>
public sealed record RuleConfig(IReadOnlyDictionary<string, RuleOverride> Overrides)
{
    public const string RelativePath = ".quality/rules.config.json";

    public static readonly RuleConfig Empty = new(new Dictionary<string, RuleOverride>(StringComparer.Ordinal));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled(string ruleId, bool defaultOn) =>
        Overrides.TryGetValue(ruleId, out var overrideValue) && overrideValue.Enabled is { } enabled
            ? enabled
            : defaultOn;

    public bool? EnabledOverride(string ruleId) =>
        Overrides.TryGetValue(ruleId, out var overrideValue) ? overrideValue.Enabled : null;

    public string EffectiveSeverity(RuleDefinition rule) =>
        Overrides.TryGetValue(rule.Id, out var overrideValue) && !string.IsNullOrWhiteSpace(overrideValue.Severity)
            ? overrideValue.Severity
            : rule.Severity;

    public static RuleConfig Load(string repositoryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<RuleConfigDocument>(stream, JsonOptions)
            ?? throw new JsonException($"Rule config '{path}' is empty.");
        if (document.SchemaVersion != 1)
            throw new JsonException($"Rule config '{path}' has an unsupported schemaVersion.");
        var overrides = document.Overrides ?? new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        var knownRuleIds = RuleLibrary.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (ruleId, ruleOverride) in overrides)
        {
            if (!knownRuleIds.Contains(ruleId))
                throw new JsonException($"Rule config '{path}' refers to unknown rule '{ruleId}'.");
            if (ruleOverride is null)
                throw new JsonException($"Rule config '{path}' override '{ruleId}' must be an object.");
            if (ruleOverride.Enabled is null && string.IsNullOrWhiteSpace(ruleOverride.Severity) &&
                string.IsNullOrWhiteSpace(ruleOverride.Reason))
                throw new JsonException($"Rule config '{path}' override '{ruleId}' is empty.");
            if (!string.IsNullOrWhiteSpace(ruleOverride.Severity) &&
                ruleOverride.Severity is not ("critical" or "high" or "medium" or "low" or "info"))
                throw new JsonException(
                    $"Rule config '{path}' override '{ruleId}' has unsupported severity '{ruleOverride.Severity}'.");
        }
        return new RuleConfig(overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    private sealed record RuleConfigDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("overrides")] IReadOnlyDictionary<string, RuleOverride>? Overrides);
}
