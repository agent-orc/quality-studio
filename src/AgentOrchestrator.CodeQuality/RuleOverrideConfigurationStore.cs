using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record RuleOverride(string RuleId, bool Enabled, string? Reason);

/// <summary>
/// Reads and writes the <c>.quality/rules.json</c> contract: per-project enable/disable
/// overrides for the built-in named rule library's default-on set. This never touches
/// custom guidelines under <c>.quality/inputs</c>, which keep their own file-based
/// <c>enabled: false</c> tombstone convention.
/// </summary>
public sealed class RuleOverrideConfigurationStore
{
    public const string Schema = "https://agent-orchestrator.dev/quality/schemas/rules-config.v1.schema.json";
    public const string ConfigurationPath = ".quality/rules.json";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;

    public RuleOverrideConfigurationStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        path = Path.Combine(Path.GetFullPath(repositoryRoot), ConfigurationPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public IReadOnlyList<RuleOverride> Read()
    {
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = document.RootElement;
        if (rootElement.ValueKind != JsonValueKind.Object ||
            rootElement.EnumerateObject().Any(property => property.Name is not ("$schema" or "overrides")) ||
            !rootElement.TryGetProperty("overrides", out var overridesElement) || overridesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{ConfigurationPath} must contain only '$schema' and an 'overrides' array.");
        if (rootElement.TryGetProperty("$schema", out var schemaElement) && schemaElement.GetString() != Schema)
            throw new InvalidDataException($"{ConfigurationPath} uses an unsupported schema.");

        var overrides = new List<RuleOverride>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in overridesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                element.EnumerateObject().Any(property => property.Name is not ("ruleId" or "enabled" or "reason")) ||
                !element.TryGetProperty("ruleId", out var ruleIdElement) || ruleIdElement.ValueKind != JsonValueKind.String ||
                !element.TryGetProperty("enabled", out var enabledElement) ||
                enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"{ConfigurationPath} contains an invalid override.");
            var ruleId = ruleIdElement.GetString()!;
            var enabled = enabledElement.GetBoolean();
            var reason = element.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            if (!enabled && string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException($"{ConfigurationPath} must give a reason when disabling '{ruleId}'.");
            if (!seen.Add(ruleId))
                throw new InvalidDataException($"{ConfigurationPath} has a duplicate override for '{ruleId}'.");
            overrides.Add(new RuleOverride(ruleId, enabled, reason));
        }
        return overrides;
    }

    public void Write(IReadOnlyList<RuleOverride> overrides)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var payload = new Dictionary<string, object?>
        {
            ["$schema"] = Schema,
            ["overrides"] = overrides.Select(value => value.Enabled
                ? new Dictionary<string, object?> { ["ruleId"] = value.RuleId, ["enabled"] = true }
                : new Dictionary<string, object?> { ["ruleId"] = value.RuleId, ["enabled"] = false, ["reason"] = value.Reason })
                .ToArray(),
        };
        var temporary = Path.Combine(directory, $"rules.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                var bytes = Utf8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions) + Environment.NewLine);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
