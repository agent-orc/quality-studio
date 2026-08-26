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
    public const string Schema = "https://quality.studio/schemas/rule-config.v1.schema.json";

    public static readonly RuleConfig Empty = new(new Dictionary<string, RuleOverride>(StringComparer.Ordinal));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public RuleOverride? GetOverride(string ruleId) => Overrides.GetValueOrDefault(ruleId);

    public bool IsEnabled(RuleDefinition rule) =>
        string.Equals(rule.Status, "active", StringComparison.Ordinal) &&
        (Overrides.TryGetValue(rule.Id, out var overrideValue) && overrideValue.Enabled is { } enabled
            ? enabled
            : rule.DefaultOn);

    public static RuleConfig Load(string repositoryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<RuleConfigDocument>(stream, JsonOptions)
            ?? throw new JsonException($"Rule config '{path}' is empty.");
        if (!string.Equals(document.Schema, Schema, StringComparison.Ordinal))
            throw new JsonException($"Rule config '{path}' has an unsupported $schema.");
        if (document.SchemaVersion != 1)
            throw new JsonException($"Rule config '{path}' has an unsupported schemaVersion.");
        var overrides = document.Overrides ?? new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        var knownRuleIds = RuleLibrary.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (ruleId, value) in overrides)
        {
            if (!knownRuleIds.Contains(ruleId))
                throw new JsonException($"Rule config '{path}' references unknown rule '{ruleId}'.");
            if (value.Severity is not null && value.Severity is not ("critical" or "high" or "medium" or "low" or "info"))
                throw new JsonException($"Rule config '{path}' has unsupported severity '{value.Severity}' for rule '{ruleId}'.");
        }
        return new RuleConfig(overrides.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
    }

    private sealed record RuleConfigDocument(
        [property: JsonPropertyName("$schema")] string? Schema,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("overrides")] IReadOnlyDictionary<string, RuleOverride>? Overrides);
}
