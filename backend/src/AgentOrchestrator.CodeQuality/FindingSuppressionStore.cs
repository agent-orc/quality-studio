using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingSuppressionMatch(string Fingerprint);

public sealed record FindingSuppressionRule(
    string Id,
    bool Enabled,
    FindingSuppressionMatch Match,
    string Effect,
    string Reason,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    string? Path = null,
    string? RuleId = null,
    string? Title = null)
{
    public bool IsActive(DateTimeOffset now) =>
        Enabled && string.Equals(Effect, "suppress", StringComparison.Ordinal) &&
        (ExpiresAt is null || ExpiresAt > now);
}

public sealed record FindingSuppressionDocument(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<FindingSuppressionRule> Rules);

public sealed class FindingSuppressionStore
{
    public const string RelativePath = ".quality/findings/suppressions.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string suppressionPath;
    private readonly Func<DateTimeOffset> clock;

    public FindingSuppressionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        suppressionPath = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string SuppressionPath => suppressionPath;

    public Task<FindingSuppressionDocument> ReadAsync(CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(() => LoadAsync(cancellationToken), cancellationToken);

    public Task<FindingSuppressionDocument> AddExactAsync(
        FindingIdentityRecord finding,
        string title,
        string author,
        string reason,
        DateTimeOffset? expiresAt = null,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(async () =>
        {
            ValidateText(author, 200, "A suppression author is required.", "A suppression author cannot exceed 200 characters.");
            ValidateText(reason, 2000, "A suppression reason is required.", "A suppression reason cannot exceed 2,000 characters.");
            if (expiresAt is not null && expiresAt <= clock().ToUniversalTime())
                throw new ArgumentException("Suppression expiry must be in the future.", nameof(expiresAt));

            var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            EnsureRevision(document, expectedRevision);
            if (document.Rules.Any(rule => string.Equals(rule.Match.Fingerprint, finding.Fingerprint, StringComparison.Ordinal)))
                throw new ArgumentException($"Finding '{finding.Fingerprint}' is already in the ignore list.", nameof(finding));

            var rule = new FindingSuppressionRule(
                "exact-" + finding.Fingerprint[7..],
                true,
                new FindingSuppressionMatch(finding.Fingerprint),
                "suppress",
                reason.Trim(),
                author.Trim(),
                clock().ToUniversalTime(),
                expiresAt?.ToUniversalTime(),
                finding.Path,
                finding.RuleId,
                string.IsNullOrWhiteSpace(title) ? null : title.Trim());
            var updated = new FindingSuppressionDocument(1, document.Revision + 1,
                document.Rules.Append(rule).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray());
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }, cancellationToken);

    public Task<FindingSuppressionDocument> DeleteAsync(
        string id,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        ExecuteLockedAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            EnsureRevision(document, expectedRevision);
            var rules = document.Rules.Where(rule => !string.Equals(rule.Id, id, StringComparison.Ordinal)).ToArray();
            if (rules.Length == document.Rules.Count) throw new KeyNotFoundException($"Suppression rule '{id}' was not found.");
            var updated = new FindingSuppressionDocument(1, document.Revision + 1, rules);
            await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return updated;
        }, cancellationToken);

    public static IReadOnlyDictionary<string, FindingSuppressionRule> ActiveByFingerprint(
        FindingSuppressionDocument document,
        DateTimeOffset? now = null) =>
        document.Rules.Where(rule => rule.IsActive(now ?? DateTimeOffset.UtcNow))
            .ToDictionary(rule => rule.Match.Fingerprint, StringComparer.Ordinal);

    private async Task<FindingSuppressionDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(suppressionPath)) return new(1, 0, []);
        await using var stream = new FileStream(suppressionPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var document = await JsonSerializer.DeserializeAsync<FindingSuppressionDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Finding suppressions must be a JSON object.");
        if (document.SchemaVersion != 1) throw new JsonException($"Unsupported finding suppression schemaVersion '{document.SchemaVersion}'.");
        if (document.Revision < 0 || document.Rules is null) throw new JsonException("Finding suppression revision or rules are invalid.");
        if (document.Rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ||
            document.Rules.GroupBy(rule => rule.Match.Fingerprint, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding suppressions contain duplicate ids or fingerprints.");
        if (document.Rules.Any(rule => string.IsNullOrWhiteSpace(rule.Id) || rule.Match is null ||
            !IsFingerprint(rule.Match.Fingerprint) || !string.Equals(rule.Effect, "suppress", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(rule.Author) || string.IsNullOrWhiteSpace(rule.Reason)))
            throw new JsonException("Finding suppressions contain an invalid exact-fingerprint rule.");
        return document;
    }

    private async Task SaveAsync(FindingSuppressionDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(suppressionPath)!);
        var temporary = suppressionPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, suppressionPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<T> ExecuteLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(suppressionPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(suppressionPath)!);
            var lockPath = suppressionPath + ".lock";
            FileStream? fileLock = null;
            while (fileLock is null)
            {
                try
                {
                    fileLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                        1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                }
                catch (IOException) { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
            }
            await using (fileLock) return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void EnsureRevision(FindingSuppressionDocument document, long? expectedRevision)
    {
        if (expectedRevision is not null && expectedRevision != document.Revision)
            throw new FindingSuppressionConflictException(document);
    }

    private static void ValidateText(string value, int maximum, string requiredMessage, string maximumMessage)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(requiredMessage);
        if (value.Length > maximum) throw new ArgumentException(maximumMessage);
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class FindingSuppressionConflictException(FindingSuppressionDocument current)
    : Exception("The finding ignore list changed after it was loaded.")
{
    public FindingSuppressionDocument Current { get; } = current;
}
