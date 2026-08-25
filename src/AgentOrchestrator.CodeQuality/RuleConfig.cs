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
    public const string SchemaId = "https://quality.studio/schemas/rule-config.v1.schema.json";

    public static readonly RuleConfig Empty = new(new Dictionary<string, RuleOverride>(StringComparer.Ordinal));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public RuleOverride? GetOverride(string ruleId) => Overrides.GetValueOrDefault(ruleId);

    public bool IsEnabled(string ruleId, bool defaultOn) =>
        Overrides.TryGetValue(ruleId, out var overrideValue) && overrideValue.Enabled is { } enabled
            ? enabled
            : defaultOn;

    public static RuleConfig Load(string repositoryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<RuleConfigDocument>(stream, JsonOptions)
            ?? throw new JsonException($"Rule config '{path}' is empty.");
        if (!string.Equals(document.Schema, SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Rule config '{path}' has an unsupported $schema.");
        if (document.SchemaVersion != 1)
            throw new JsonException($"Rule config '{path}' has an unsupported schemaVersion.");
        var overrides = document.Overrides ?? new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        var knownRules = RuleLibrary.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (ruleId, value) in overrides)
        {
            if (!knownRules.Contains(ruleId))
                throw new JsonException($"Rule config '{path}' overrides unknown rule '{ruleId}'.");
            if (value.Enabled is null && value.Severity is null && value.Reason is null)
                throw new JsonException($"Rule config '{path}' has an empty override for '{ruleId}'.");
            if (value.Severity is not null && !RuleLibrary.IsSeverity(value.Severity))
                throw new JsonException($"Rule config '{path}' has unsupported severity '{value.Severity}' for '{ruleId}'.");
            if (value.Reason is { Length: > 400 })
                throw new JsonException($"Rule config '{path}' has a reason longer than 400 characters for '{ruleId}'.");
        }
        return new RuleConfig(new Dictionary<string, RuleOverride>(overrides, StringComparer.Ordinal));
    }

    private sealed record RuleConfigDocument(
        [property: JsonPropertyName("$schema")] string? Schema,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("overrides")] IReadOnlyDictionary<string, RuleOverride>? Overrides);
}
