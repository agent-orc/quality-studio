using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record RepositoryScopeRule(string Action, string Pattern, string? Reason = null);

public sealed record RepositoryScopeConfiguration(string Schema, IReadOnlyList<RepositoryScopeRule> Rules);

public sealed record RepositoryScopeRulePreview(
    RepositoryScopeRule Rule,
    IReadOnlyList<string> MatchedFiles,
    bool WiderPattern);

/// <summary>
/// Atomically manages the existing ordered <c>.quality/scope.json</c> contract. This is the only
/// mutation boundary for the operator scope surface; browser callers never write repository files.
/// </summary>
public sealed class RepositoryScopeConfigurationStore
{
    public const string Schema = "https://agent-orchestrator.dev/quality/schemas/scope.v1.schema.json";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string root;
    private readonly string path;
    private readonly object gate;

    public RepositoryScopeConfigurationStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        root = Path.GetFullPath(repositoryRoot);
        path = Path.Combine(root, RepositoryScope.ConfigurationPath.Replace('/', Path.DirectorySeparatorChar));
        gate = Gates.GetOrAdd(root, _ => new object());
    }

    public RepositoryScopeConfiguration Read()
    {
        lock (gate) return ReadCore();
    }

    public RepositoryScopeRulePreview Preview(RepositoryScopeRule rule, IEnumerable<string> candidateFiles)
    {
        var normalized = Validate(rule);
        var files = candidateFiles.Select(Canonical).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var matches = files.Where(candidate => RepositoryScope.PatternMatches(normalized.Pattern, candidate)).ToArray();
        return new RepositoryScopeRulePreview(normalized, matches, HasPatternSyntax(normalized.Pattern) || matches.Length > 1);
    }

    public RepositoryScopeConfiguration Add(
        RepositoryScopeRule rule,
        IEnumerable<string> candidateFiles,
        bool confirmExpansion)
    {
        lock (gate)
        {
            var preview = Preview(rule, candidateFiles);
            RequireExpansionConfirmation(preview, confirmExpansion);
            var current = ReadCore();
            return WriteCore(current.Rules.Append(preview.Rule).ToArray());
        }
    }

    public RepositoryScopeConfiguration Update(
        int index,
        RepositoryScopeRule rule,
        IEnumerable<string> candidateFiles,
        bool confirmExpansion)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (index < 0 || index >= current.Rules.Count) throw new KeyNotFoundException($"Scope rule {index} was not found.");
            var preview = Preview(rule, candidateFiles);
            RequireExpansionConfirmation(preview, confirmExpansion);
            var rules = current.Rules.ToArray();
            rules[index] = preview.Rule;
            return WriteCore(rules);
        }
    }

    public RepositoryScopeConfiguration Delete(int index)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (index < 0 || index >= current.Rules.Count) throw new KeyNotFoundException($"Scope rule {index} was not found.");
            return WriteCore(current.Rules.Where((_, candidate) => candidate != index).ToArray());
        }
    }

    private RepositoryScopeConfiguration ReadCore()
    {
        if (!File.Exists(path)) return new RepositoryScopeConfiguration(Schema, []);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var rootElement = document.RootElement;
        if (rootElement.ValueKind != JsonValueKind.Object ||
            rootElement.EnumerateObject().Any(property => property.Name is not ("$schema" or "rules")) ||
            !rootElement.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{RepositoryScope.ConfigurationPath} must contain only '$schema' and a 'rules' array.");
        if (rootElement.TryGetProperty("$schema", out var schemaElement) && schemaElement.GetString() != Schema)
            throw new InvalidDataException($"{RepositoryScope.ConfigurationPath} uses an unsupported schema.");

        var rules = new List<RepositoryScopeRule>();
        foreach (var element in rulesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                element.EnumerateObject().Any(property => property.Name is not ("action" or "pattern" or "reason")) ||
                !element.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                !element.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"{RepositoryScope.ConfigurationPath} contains an invalid rule.");
            var reason = element.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            rules.Add(Validate(new RepositoryScopeRule(action.GetString()!, pattern.GetString()!, reason)));
        }
        return new RepositoryScopeConfiguration(Schema, rules);
    }

    private RepositoryScopeConfiguration WriteCore(IReadOnlyList<RepositoryScopeRule> rules)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new Dictionary<string, object?>
        {
            ["$schema"] = Schema,
            ["rules"] = rules.Select(rule => rule.Action == "exclude"
                ? new Dictionary<string, object?> { ["action"] = rule.Action, ["pattern"] = rule.Pattern, ["reason"] = rule.Reason }
                : new Dictionary<string, object?> { ["action"] = rule.Action, ["pattern"] = rule.Pattern }).ToArray(),
        };
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"scope.{Guid.NewGuid():N}.tmp");
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
        return new RepositoryScopeConfiguration(Schema, rules);
    }

    private static RepositoryScopeRule Validate(RepositoryScopeRule rule)
    {
        var action = rule.Action.Trim().ToLowerInvariant();
        var pattern = rule.Pattern.Replace('\\', '/').TrimStart('/').Trim();
        var reason = rule.Reason?.Trim();
        if (action is not ("include" or "exclude")) throw new ArgumentException("Scope action must be include or exclude.");
        if (pattern.Length is 0 or > 500) throw new ArgumentException("Scope pattern must contain 1 to 500 characters.");
        if (pattern.Contains("..", StringComparison.Ordinal)) throw new ArgumentException("Scope patterns cannot contain parent traversal.");
        if (action == "exclude" && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Exclude rules require a reason.");
        if (reason?.Length > 500) throw new ArgumentException("Scope rule reason must be at most 500 characters.");
        return new RepositoryScopeRule(action, pattern, action == "exclude" ? reason : null);
    }

    private static void RequireExpansionConfirmation(RepositoryScopeRulePreview preview, bool confirmed)
    {
        if (preview.WiderPattern && !confirmed)
            throw new ArgumentException($"Pattern '{preview.Rule.Pattern}' is wider than one exact path; preview and explicitly confirm expansion.");
    }

    private static string Canonical(string path) => path.Replace('\\', '/').TrimStart('/').TrimEnd('/');
    private static bool HasPatternSyntax(string pattern) => pattern.IndexOfAny(['*', '?', '[', ']']) >= 0 || pattern.EndsWith('/');
}
