using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum FindingState { Open, Accepted, Waived, FalsePositive, Resolved }

public sealed record FindingStateRecord(
    string Fingerprint,
    string FindingId,
    string Path,
    string RuleId,
    FindingState State,
    string Author,
    string Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ExpiresAt = null);

public sealed record FindingStateDocument(int SchemaVersion, long Revision, IReadOnlyList<FindingStateRecord> Findings);

public sealed class FindingStateStore
{
    public const string RelativePath = ".quality/findings/state.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string statePath;
    private readonly Func<DateTimeOffset> clock;

    public FindingStateStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        statePath = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "findings", "state.json");
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string StatePath => statePath;

    public async Task<IReadOnlyDictionary<string, FindingStateRecord>> ReadAsync(CancellationToken cancellationToken = default) =>
        await ExecuteLockedAsync(async () =>
        {
            var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var (effective, changed) = ReopenExpired(document, clock().ToUniversalTime());
            if (changed) await SaveAsync(effective, cancellationToken).ConfigureAwait(false);
            return ToLookup(effective);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, FindingStateRecord>> MergeReviewAsync(
        IReadOnlyCollection<FindingIdentityRecord> current,
        IReadOnlyCollection<FindingIdentityRecord> previous,
        string author,
        CancellationToken cancellationToken = default) =>
        await ExecuteLockedAsync(async () =>
        {
            var now = clock().ToUniversalTime();
            var (document, expiredChanged) = ReopenExpired(await LoadAsync(cancellationToken).ConfigureAwait(false), now);
            var records = document.Findings.ToDictionary(record => record.Fingerprint, StringComparer.Ordinal);
            var changed = expiredChanged;
            var currentFingerprints = current.Select(item => item.Fingerprint).ToHashSet(StringComparer.Ordinal);

            foreach (var finding in current)
            {
                if (!records.TryGetValue(finding.Fingerprint, out var existing) || existing.State == FindingState.Resolved)
                {
                    records[finding.Fingerprint] = NewRecord(finding, FindingState.Open, author,
                        existing is null ? "First observed by review." : "Finding reappeared in review.", now);
                    changed = true;
                }
            }

            foreach (var finding in previous.Where(item => !currentFingerprints.Contains(item.Fingerprint)))
            {
                if (!records.TryGetValue(finding.Fingerprint, out var existing) || existing.State != FindingState.Resolved)
                {
                    records[finding.Fingerprint] = NewRecord(finding, FindingState.Resolved, author,
                        "Finding was not present in the latest review.", now);
                    changed = true;
                }
            }

            if (changed)
            {
                document = new(1, document.Revision + 1, records.Values.OrderBy(record => record.Fingerprint, StringComparer.Ordinal).ToArray());
                await SaveAsync(document, cancellationToken).ConfigureAwait(false);
            }
            return ToLookup(document);
        }, cancellationToken).ConfigureAwait(false);

    public async Task<FindingStateRecord> SetAsync(
        string fingerprint,
        FindingState state,
        string author,
        string reason,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? expectedTimestamp = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteLockedAsync(async () =>
        {
            if (state == FindingState.Resolved) throw new ArgumentException("Resolved is set by review merge, not manually.", nameof(state));
            if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("A state author is required.", nameof(author));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A state reason is required.", nameof(reason));
            if (author.Length > 200) throw new ArgumentException("A state author cannot exceed 200 characters.", nameof(author));
            if (reason.Length > 2000) throw new ArgumentException("A state reason cannot exceed 2,000 characters.", nameof(reason));
            var now = clock().ToUniversalTime();
            if (expiresAt is not null && expiresAt <= now) throw new ArgumentException("Finding state expiry must be in the future.", nameof(expiresAt));

            var (document, _) = ReopenExpired(await LoadAsync(cancellationToken).ConfigureAwait(false), now);
            var records = document.Findings.ToDictionary(record => record.Fingerprint, StringComparer.Ordinal);
            if (!records.TryGetValue(fingerprint, out var existing)) throw new KeyNotFoundException($"Finding '{fingerprint}' was not found.");
            if (expectedTimestamp is not null && expectedTimestamp.Value != existing.Timestamp)
                throw new FindingStateConflictException(fingerprint, existing);

            var updated = existing with
            {
                State = state,
                Author = author.Trim(),
                Reason = reason.Trim(),
                Timestamp = now,
                ExpiresAt = expiresAt?.ToUniversalTime(),
            };
            records[fingerprint] = updated;
            await SaveAsync(new(1, document.Revision + 1,
                records.Values.OrderBy(record => record.Fingerprint, StringComparer.Ordinal).ToArray()), cancellationToken).ConfigureAwait(false);
            return updated;
        }, cancellationToken).ConfigureAwait(false);

    private static FindingStateRecord NewRecord(FindingIdentityRecord finding, FindingState state, string author, string reason, DateTimeOffset now) =>
        new(finding.Fingerprint, finding.Id, finding.Path, finding.RuleId, state, author, reason, now);

    private async Task<FindingStateDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath)) return new(1, 0, []);
        await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var document = await JsonSerializer.DeserializeAsync<FindingStateDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Finding state must be a JSON object.");
        if (document.SchemaVersion != 1) throw new JsonException($"Unsupported finding state schemaVersion '{document.SchemaVersion}'.");
        if (document.Revision < 0 || document.Findings is null) throw new JsonException("Finding state revision or findings is invalid.");
        if (document.Findings.GroupBy(record => record.Fingerprint, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding state contains duplicate fingerprints.");
        if (document.Findings.Any(record => !IsFingerprint(record.Fingerprint) ||
            string.IsNullOrWhiteSpace(record.FindingId) || string.IsNullOrWhiteSpace(record.Path) ||
            string.IsNullOrWhiteSpace(record.RuleId) || string.IsNullOrWhiteSpace(record.Author) ||
            string.IsNullOrWhiteSpace(record.Reason)))
            throw new JsonException("Finding state contains an invalid record.");
        return document;
    }

    private async Task SaveAsync(FindingStateDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, statePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (FindingStateDocument Document, bool Changed) ReopenExpired(FindingStateDocument document, DateTimeOffset now)
    {
        var changed = false;
        var records = document.Findings.Select(record =>
        {
            if (record.ExpiresAt is null || record.ExpiresAt > now || record.State is FindingState.Open or FindingState.Resolved) return record;
            changed = true;
            return record with
            {
                State = FindingState.Open,
                Author = "quality-studio",
                Reason = $"{StateName(record.State)} state expired.",
                Timestamp = now,
                ExpiresAt = null,
            };
        }).ToArray();
        return (changed ? new(1, document.Revision + 1, records) : document, changed);
    }

    private async Task<T> ExecuteLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(statePath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var lockPath = statePath + ".lock";
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

    public static string StateName(FindingState state) => state switch
    {
        FindingState.FalsePositive => "false-positive",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static bool IsFingerprint(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyDictionary<string, FindingStateRecord> ToLookup(FindingStateDocument document) =>
        document.Findings.ToDictionary(record => record.Fingerprint, StringComparer.Ordinal);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new FindingStateConverter());
        return options;
    }

    private sealed class FindingStateConverter : JsonConverter<FindingState>
    {
        public override FindingState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() switch
            {
                "open" => FindingState.Open,
                "accepted" => FindingState.Accepted,
                "waived" => FindingState.Waived,
                "false-positive" => FindingState.FalsePositive,
                "resolved" => FindingState.Resolved,
                var value => throw new JsonException($"Unsupported finding state '{value}'."),
            };

        public override void Write(Utf8JsonWriter writer, FindingState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(StateName(value));
    }
}

public sealed class FindingStateConflictException(string fingerprint, FindingStateRecord current)
    : Exception($"Finding '{fingerprint}' changed after it was loaded.")
{
    public FindingStateRecord Current { get; } = current;
}
