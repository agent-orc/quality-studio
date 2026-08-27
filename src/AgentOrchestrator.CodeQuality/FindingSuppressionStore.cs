using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingSuppression(
    string Fingerprint,
    string FindingId,
    string Path,
    string RuleId,
    string Title,
    string Severity,
    string Reason,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record FindingSuppressionDocument(int SchemaVersion, long Revision, IReadOnlyList<FindingSuppression> Suppressions);

/// <summary>
/// Repository-owned, fingerprint-keyed "ignore list". Suppression is independent from the
/// accepted/waived/false-positive lifecycle in <see cref="FindingStateStore"/>: it only controls
/// whether a finding is hidden from the default queue. It never deletes the observation and never
/// changes the effective grade.
/// </summary>
public sealed class FindingSuppressionStore
{
    public const string RelativePath = ".quality/findings/suppressions.json";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly object gate;
    private readonly Func<DateTimeOffset> clock;

    public FindingSuppressionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        path = Path.Combine(root, ".quality", "findings", "suppressions.json");
        gate = Gates.GetOrAdd(root, _ => new object());
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string StatePath => path;

    public IReadOnlyDictionary<string, FindingSuppression> Read()
    {
        lock (gate)
        {
            var (document, changed) = DropExpired(ReadCore(), clock().ToUniversalTime());
            if (changed) WriteCore(document);
            return ToLookup(document);
        }
    }

    public FindingSuppression Add(FindingSuppression suppression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.Author);
        ArgumentException.ThrowIfNullOrWhiteSpace(suppression.Reason);
        if (suppression.Author.Length > 200) throw new ArgumentException("An ignore author cannot exceed 200 characters.", nameof(suppression));
        if (suppression.Reason.Length > 2000) throw new ArgumentException("An ignore reason cannot exceed 2,000 characters.", nameof(suppression));
        var now = clock().ToUniversalTime();
        if (suppression.ExpiresAt is not null && suppression.ExpiresAt <= now)
            throw new ArgumentException("Ignore expiry must be in the future.", nameof(suppression));

        lock (gate)
        {
            var (document, _) = DropExpired(ReadCore(), now);
            var normalized = suppression with
            {
                Author = suppression.Author.Trim(),
                Reason = suppression.Reason.Trim(),
                CreatedAt = now,
                ExpiresAt = suppression.ExpiresAt?.ToUniversalTime(),
            };
            var remaining = document.Suppressions.Where(item => item.Fingerprint != normalized.Fingerprint);
            WriteCore(new(1, document.Revision + 1,
                remaining.Append(normalized).OrderBy(item => item.Fingerprint, StringComparer.Ordinal).ToArray()));
            return normalized;
        }
    }

    public bool Remove(string fingerprint)
    {
        lock (gate)
        {
            var (document, _) = DropExpired(ReadCore(), clock().ToUniversalTime());
            var remaining = document.Suppressions.Where(item => item.Fingerprint != fingerprint).ToArray();
            if (remaining.Length == document.Suppressions.Count) return false;
            WriteCore(new(1, document.Revision + 1, remaining));
            return true;
        }
    }

    private FindingSuppressionDocument ReadCore()
    {
        if (!File.Exists(path)) return new(1, 0, []);
        var document = JsonSerializer.Deserialize<FindingSuppressionDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Finding suppressions must be a JSON object.");
        if (document.SchemaVersion != 1) throw new JsonException($"Unsupported finding suppressions schemaVersion '{document.SchemaVersion}'.");
        if (document.Suppressions.GroupBy(item => item.Fingerprint, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding suppressions contain duplicate fingerprints.");
        return document;
    }

    private void WriteCore(FindingSuppressionDocument document)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"suppressions.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                var bytes = Utf8.GetBytes(JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
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

    private static (FindingSuppressionDocument Document, bool Changed) DropExpired(FindingSuppressionDocument document, DateTimeOffset now)
    {
        var remaining = document.Suppressions.Where(item => item.ExpiresAt is null || item.ExpiresAt > now).ToArray();
        return remaining.Length == document.Suppressions.Count ? (document, false) : (new(1, document.Revision + 1, remaining), true);
    }

    private static IReadOnlyDictionary<string, FindingSuppression> ToLookup(FindingSuppressionDocument document) =>
        document.Suppressions.ToDictionary(item => item.Fingerprint, StringComparer.Ordinal);
}
