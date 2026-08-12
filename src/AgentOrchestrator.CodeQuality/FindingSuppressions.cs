using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingSuppressionRule(
    string Id,
    string Fingerprint,
    string Reason,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record FindingSuppressionDocument(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<FindingSuppressionRule> Rules);

public sealed class FindingSuppressionConflictException(long currentRevision)
    : Exception("Finding ignore list changed after it was loaded.")
{
    public long CurrentRevision { get; } = currentRevision;
}

/// <summary>Repository-owned, revisioned exact-finding ignore policy.</summary>
public sealed class FindingSuppressionStore
{
    public const string RelativePath = ".quality/findings/suppressions.json";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly object gate;
    private readonly Func<DateTimeOffset> clock;

    public FindingSuppressionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        path = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        gate = Gates.GetOrAdd(path, _ => new object());
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FindingSuppressionDocument Read()
    {
        lock (gate) return ReadCore();
    }

    public FindingSuppressionDocument AddExact(
        string fingerprint,
        string author,
        string reason,
        DateTimeOffset? expiresAt,
        long expectedRevision)
    {
        ValidateFingerprint(fingerprint);
        ValidateText(author, reason);
        var createdAt = clock().ToUniversalTime();
        if (expiresAt is not null && expiresAt <= createdAt)
            throw new ArgumentException("Ignore expiry must be in the future.");

        lock (gate)
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new FindingSuppressionConflictException(current.Revision);
            if (current.Rules.Any(rule => rule.Fingerprint == fingerprint &&
                (rule.ExpiresAt is null || rule.ExpiresAt > createdAt)))
                throw new ArgumentException("This finding is already on the ignore list.");
            var rule = new FindingSuppressionRule(
                $"exact-{fingerprint[7..19]}", fingerprint, reason.Trim(), author.Trim(), createdAt, expiresAt);
            return Write(current with
            {
                Revision = current.Revision + 1,
                Rules = current.Rules.Where(existing => existing.Fingerprint != fingerprint).Append(rule).ToArray(),
            });
        }
    }

    public FindingSuppressionDocument Remove(string id, long expectedRevision)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new FindingSuppressionConflictException(current.Revision);
            if (!current.Rules.Any(rule => rule.Id == id))
                throw new KeyNotFoundException($"Ignore rule '{id}' was not found.");
            return Write(current with
            {
                Revision = current.Revision + 1,
                Rules = current.Rules.Where(rule => rule.Id != id).ToArray(),
            });
        }
    }

    public static FindingSuppressionRule? Match(
        FindingSuppressionDocument document,
        string? fingerprint,
        DateTimeOffset now) =>
        fingerprint is null ? null : document.Rules.FirstOrDefault(rule =>
            rule.Fingerprint == fingerprint && (rule.ExpiresAt is null || rule.ExpiresAt > now));

    private FindingSuppressionDocument ReadCore()
    {
        if (!File.Exists(path)) return new FindingSuppressionDocument(1, 0, []);
        var document = JsonSerializer.Deserialize<FindingSuppressionDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Finding ignore list must be a JSON object.");
        if (document.SchemaVersion != 1 || document.Revision < 0 || document.Rules is null)
            throw new JsonException("Finding ignore list uses an unsupported schema or invalid revision.");
        if (document.Rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding ignore list contains duplicate rule ids.");
        foreach (var rule in document.Rules)
        {
            ValidateFingerprint(rule.Fingerprint);
            ValidateText(rule.Author, rule.Reason);
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 200)
                throw new JsonException("Finding ignore rule id is invalid.");
            if (rule.ExpiresAt is not null && rule.ExpiresAt <= rule.CreatedAt)
                throw new JsonException("Finding ignore expiry must follow creation.");
        }
        return document;
    }

    private FindingSuppressionDocument Write(FindingSuppressionDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return document;
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 71 || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
            !fingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Finding fingerprint must be a lowercase SHA-256 value.");
    }

    private static void ValidateText(string author, string reason)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > 200)
            throw new ArgumentException("Ignore author must contain 1 to 200 characters.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new ArgumentException("Ignore reason must contain 1 to 2,000 characters.");
    }
}

public static class FindingSuppressionProjection
{
    public static JsonObject Apply(JsonObject metadata, FindingSuppressionDocument suppressions, DateTimeOffset now)
    {
        var result = metadata.DeepClone().AsObject();
        var suppressedCount = 0;
        var includedWeight = 0;
        var newlySuppressedWeight = 0;
        foreach (var finding in result["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var fingerprint = finding["fingerprint"]?.GetValue<string>();
            var match = FindingSuppressionStore.Match(suppressions, fingerprint, now);
            finding["suppressed"] = match is not null;
            if (match is null)
            {
                if (AffectsGrade(finding)) includedWeight += SeverityWeight(finding["severity"]?.GetValue<string>());
                finding.Remove("suppression");
                continue;
            }
            suppressedCount++;
            if (AffectsGrade(finding)) newlySuppressedWeight += SeverityWeight(finding["severity"]?.GetValue<string>());
            finding["suppression"] = JsonSerializer.SerializeToNode(match, JsonOptions);
        }
        var counts = result["findingCounts"] as JsonObject ?? [];
        result["findingCounts"] = counts;
        counts["suppressed"] = suppressedCount;
        ApplyEffectiveGrade(result, includedWeight, newlySuppressedWeight);
        return result;
    }

    private static void ApplyEffectiveGrade(JsonObject metadata, int includedWeight, int suppressedWeight)
    {
        if (suppressedWeight == 0 || metadata["grade"] is not JsonObject grade ||
            grade["score"] is not JsonValue scoreNode || !scoreNode.TryGetValue<int>(out var currentScore)) return;
        var totalWeight = includedWeight + suppressedWeight;
        var adjusted = totalWeight == 0
            ? 100
            : (int)Math.Round(100 - (100 - currentScore) * (includedWeight / (double)totalWeight), MidpointRounding.AwayFromZero);
        adjusted = Math.Clamp(adjusted, currentScore, 100);
        if (metadata["security"] is JsonObject security && security["verdict"]?.GetValue<string>() is { } verdict)
            adjusted = Math.Min(adjusted, verdict switch { "warn" => 79, "block" or "unavailable" => 59, _ => 100 });
        grade["score"] = adjusted;
        grade["band"] = adjusted switch { >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 60 => "D", _ => "F" };
        grade["rationale"] = (grade["rationale"]?.GetValue<string>() ?? string.Empty) +
            " Ignored findings are excluded from this effective grade.";
    }

    private static bool AffectsGrade(JsonObject finding) =>
        finding["state"]?.GetValue<string>() is not ("waived" or "false-positive" or "resolved");

    private static int SeverityWeight(string? severity) => severity switch
    {
        "critical" => 16,
        "high" => 8,
        "medium" => 4,
        "low" => 2,
        _ => 1,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
