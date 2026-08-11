using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingSuppressionMatch(
    string? Fingerprint = null,
    string? RuleId = null,
    string? PathPattern = null,
    IReadOnlyList<string>? ReviewKinds = null,
    IReadOnlyList<string>? SourceKinds = null);

public sealed record FindingSuppressionRule(
    string Id,
    bool Enabled,
    FindingSuppressionMatch Match,
    string Effect,
    string Reason,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record FindingSuppressionDocument(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<FindingSuppressionRule> Rules);

public sealed record FindingSuppressionCandidate(
    string Fingerprint,
    string RuleId,
    string Path,
    string ReviewKind,
    string SourceKind);

/// <summary>
/// Revisioned repository-owned ignore policy. Rules affect presentation, never
/// delete the finding observation, and therefore survive replacement review runs.
/// </summary>
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
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        path = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        gate = Gates.GetOrAdd(path, _ => new object());
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FindingSuppressionDocument Read()
    {
        lock (gate) return ReadCore();
    }

    public FindingSuppressionDocument Add(FindingSuppressionRule rule, long expectedRevision)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new FindingSuppressionConflictException(current.Revision);
            var normalized = Validate(rule);
            if (normalized.Match.Fingerprint is null)
                throw new ArgumentException("The finding-level ignore API requires an exact fingerprint.");
            if (current.Rules.Any(existing => existing.Id == normalized.Id))
                throw new ArgumentException($"Suppression rule '{normalized.Id}' already exists.");
            if (current.Rules.Any(existing => existing.Enabled && existing.Match.Fingerprint == normalized.Match.Fingerprint &&
                                              (existing.ExpiresAt is null || existing.ExpiresAt > clock().ToUniversalTime())))
                throw new ArgumentException("This finding is already on the ignore list.");
            return Write(current with
            {
                Revision = current.Revision + 1,
                Rules = current.Rules.Append(normalized).ToArray(),
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
                throw new KeyNotFoundException($"Suppression rule '{id}' was not found.");
            return Write(current with
            {
                Revision = current.Revision + 1,
                Rules = current.Rules.Where(rule => rule.Id != id).ToArray(),
            });
        }
    }

    public bool IsSuppressed(FindingSuppressionCandidate candidate) =>
        Read().Rules.Any(rule => Matches(rule, candidate, clock().ToUniversalTime()));

    public static bool Matches(FindingSuppressionRule rule, FindingSuppressionCandidate candidate, DateTimeOffset now)
    {
        if (!rule.Enabled || rule.Effect != "suppress" || rule.ExpiresAt is not null && rule.ExpiresAt <= now)
            return false;
        var match = rule.Match;
        return (match.Fingerprint is null || match.Fingerprint == candidate.Fingerprint) &&
               (match.RuleId is null || match.RuleId == candidate.RuleId) &&
               (match.PathPattern is null || RepositoryScope.PatternMatches(match.PathPattern, candidate.Path)) &&
               (match.ReviewKinds is not { Count: > 0 } || match.ReviewKinds.Contains(candidate.ReviewKind, StringComparer.Ordinal)) &&
               (match.SourceKinds is not { Count: > 0 } || match.SourceKinds.Contains(candidate.SourceKind, StringComparer.Ordinal));
    }

    private FindingSuppressionDocument ReadCore()
    {
        if (!File.Exists(path)) return new FindingSuppressionDocument(1, 0, []);
        var document = JsonSerializer.Deserialize<FindingSuppressionDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Finding suppressions must be a JSON object.");
        if (document.SchemaVersion != 1 || document.Revision < 0 || document.Rules is null)
            throw new JsonException("Finding suppressions use an unsupported schema or invalid revision.");
        if (document.Rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding suppressions contain duplicate rule ids.");
        return document with { Rules = document.Rules.Select(Validate).ToArray() };
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

    private static FindingSuppressionRule Validate(FindingSuppressionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 200 ||
            rule.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Suppression id must contain 1 to 200 safe identifier characters.");
        if (rule.Effect != "suppress") throw new ArgumentException("Suppression effect must be suppress.");
        if (string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(rule.Author) || rule.Author.Length > 200)
            throw new ArgumentException("Suppression author and reason are required and bounded.");
        if (rule.ExpiresAt is not null && rule.ExpiresAt <= rule.CreatedAt)
            throw new ArgumentException("Suppression expiry must be after its creation time.");
        var match = rule.Match;
        if (match.Fingerprint is not null) ValidateFingerprint(match.Fingerprint);
        if (match.Fingerprint is null && string.IsNullOrWhiteSpace(match.RuleId) && string.IsNullOrWhiteSpace(match.PathPattern) &&
            match.ReviewKinds is not { Count: > 0 } && match.SourceKinds is not { Count: > 0 })
            throw new ArgumentException("Suppression requires at least one stable match field.");
        if (match.PathPattern?.Contains("..", StringComparison.Ordinal) == true)
            throw new ArgumentException("Suppression path patterns cannot traverse parents.");
        if ((match.ReviewKinds ?? []).Any(kind => kind is not ("code" or "security" or "performance")) ||
            (match.SourceKinds ?? []).Any(kind => kind is not ("agent" or "deterministic")))
            throw new ArgumentException("Suppression review/source kinds are invalid.");
        return rule with
        {
            Id = rule.Id.Trim(),
            Reason = rule.Reason.Trim(),
            Author = rule.Author.Trim(),
            Match = match with
            {
                RuleId = Text(match.RuleId),
                PathPattern = Text(match.PathPattern)?.Replace('\\', '/').TrimStart('/'),
                ReviewKinds = (match.ReviewKinds ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                SourceKinds = (match.SourceKinds ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            },
        };
    }

    private static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 71 || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
            !fingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Finding fingerprint must be a lowercase SHA-256 value.");
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class FindingSuppressionConflictException(long currentRevision)
    : Exception("Finding suppression policy changed after it was loaded.")
{
    public long CurrentRevision { get; } = currentRevision;
}
