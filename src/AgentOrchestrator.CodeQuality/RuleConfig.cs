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
    public const string SchemaUri = "https://quality.studio/schemas/rule-config.v1.schema.json";

    public static readonly RuleConfig Empty = new(new Dictionary<string, RuleOverride>(StringComparer.Ordinal));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public bool IsEnabled(string ruleId, bool defaultOn) =>
        Overrides.TryGetValue(ruleId, out var overrideValue) && overrideValue.Enabled is { } enabled
            ? enabled
            : defaultOn;

    public RuleOverride? GetOverride(string ruleId) => Overrides.GetValueOrDefault(ruleId);

    public static RuleConfig Load(string repositoryRoot)
    {
        var path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<RuleConfigDocument>(stream, JsonOptions)
            ?? throw new JsonException($"Rule config '{path}' is empty.");
        if (!string.Equals(document.Schema, SchemaUri, StringComparison.Ordinal))
            throw new JsonException($"Rule config '{path}' must declare $schema '{SchemaUri}'.");
        if (document.SchemaVersion != 1)
            throw new JsonException($"Rule config '{path}' has an unsupported schemaVersion.");
        var overrides = new Dictionary<string, RuleOverride>(StringComparer.Ordinal);
        foreach (var (ruleId, value) in document.Overrides ?? new Dictionary<string, RuleOverride>())
        {
            try
            {
                _ = RuleLibrary.Get(ruleId);
            }
            catch (KeyNotFoundException exception)
            {
                throw new JsonException($"Rule config '{path}' refers to unknown rule '{ruleId}'.", exception);
            }
            if (value is null)
                throw new JsonException($"Rule config '{path}' has a null override for '{ruleId}'.");
            if (value.Enabled is null && value.Severity is null && value.Reason is null)
                throw new JsonException($"Rule config '{path}' has an empty override for '{ruleId}'.");
            if (value.Severity is not null && value.Severity is not ("critical" or "high" or "medium" or "low" or "info"))
                throw new JsonException($"Rule config '{path}' has an unsupported severity '{value.Severity}' for '{ruleId}'.");
            if (value.Reason is { Length: > 400 })
                throw new JsonException($"Rule config '{path}' has a reason longer than 400 characters for '{ruleId}'.");
            overrides.Add(ruleId, value);
        }
        return new RuleConfig(overrides);
    }

    private sealed record RuleConfigDocument(
        [property: JsonPropertyName("$schema")] string? Schema,
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("overrides")] IReadOnlyDictionary<string, RuleOverride>? Overrides);
}
