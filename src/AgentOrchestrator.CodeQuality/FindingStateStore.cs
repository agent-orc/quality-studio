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
    DateTimeOffset? ExpiresAt = null,
    string? IssueId = null,
    string? OccurrenceFingerprint = null,
    string? FingerprintAlgorithm = null,
    IReadOnlyList<string>? LegacyFingerprints = null);

public sealed record FindingStateDocument(int SchemaVersion, long Revision, IReadOnlyList<FindingStateRecord> Findings);

/// <summary>
/// Compatibility projection over the append-only issue lifecycle ledger. Existing v1 snapshots remain
/// readable and are imported once; all new state transitions are recorded as immutable events first.
/// </summary>
public sealed class FindingStateStore
{
    public const string RelativePath = ".quality/findings/state.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string statePath;
    private readonly IssueLifecycleStore lifecycle;
    private readonly Func<DateTimeOffset> clock;

    public FindingStateStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        statePath = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "findings", "state.json");
        lifecycle = new IssueLifecycleStore(repositoryRoot);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string StatePath => statePath;

    public string LifecyclePath => lifecycle.Path;

    public async Task<IReadOnlyDictionary<string, FindingStateRecord>> ReadAsync(
        CancellationToken cancellationToken = default) => await ExecuteLockedAsync(async () =>
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLifecycleInitializedAsync(document, cancellationToken).ConfigureAwait(false);
        var states = await ReadWithExpiryAsync(cancellationToken).ConfigureAwait(false);
        var projected = Project(document, states);
        if (!SnapshotEquivalent(document, projected)) await SaveAsync(projected, cancellationToken).ConfigureAwait(false);
        return ToLookup(projected);
    }, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, FindingStateRecord>> MergeReviewAsync(
        IReadOnlyCollection<FindingIdentityRecord> current,
        IReadOnlyCollection<FindingIdentityRecord> previous,
        string author,
        CancellationToken cancellationToken = default) => await ExecuteLockedAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(previous);
        if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("A state author is required.", nameof(author));
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLifecycleInitializedAsync(document, cancellationToken).ConfigureAwait(false);
        var now = clock().ToUniversalTime();
        var states = await ReadWithExpiryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in current.Select(value => value.WithV2Identity()))
        {
            if (!states.TryGetValue(item.IssueId!, out var existing) || existing.State == "resolved")
            {
                var lifecycleEvent = IssueLifecycleEvent.Create(
                    item,
                    CoreQualityTerms.Lifecycles.Open,
                    "agent",
                    author,
                    existing is null ? "First observed by review." : "Finding reappeared in review.",
                    now);
                await lifecycle.AppendAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        // Omission is deliberately not a lifecycle transition. Resolution requires ResolveAsync with
        // a human action or a versioned reconciliation policy and basis observations.
        states = await lifecycle.ReadAsync(cancellationToken).ConfigureAwait(false);
        var projected = Project(document, states);
        if (!SnapshotEquivalent(document, projected)) await SaveAsync(projected, cancellationToken).ConfigureAwait(false);
        return ToLookup(projected);
    }, cancellationToken).ConfigureAwait(false);

    public async Task<FindingStateRecord> SetAsync(
        string fingerprint,
        FindingState state,
        string author,
        string reason,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? expectedTimestamp = null,
        CancellationToken cancellationToken = default) => await ExecuteLockedAsync(async () =>
    {
        if (state == FindingState.Resolved)
            throw new ArgumentException("Resolved requires an explicit human or policy-backed resolution event.", nameof(state));
        ValidateActor(author, reason);
        var now = clock().ToUniversalTime();
        if (expiresAt is not null && expiresAt <= now)
            throw new ArgumentException("Finding state expiry must be in the future.", nameof(expiresAt));
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLifecycleInitializedAsync(document, cancellationToken).ConfigureAwait(false);
        var states = await ReadWithExpiryAsync(cancellationToken).ConfigureAwait(false);
        var existing = FindByFingerprint(states, fingerprint)
            ?? throw new KeyNotFoundException($"Finding '{fingerprint}' was not found.");
        if (expectedTimestamp is not null && expectedTimestamp.Value != existing.Timestamp)
            throw new FindingStateConflictException(fingerprint, ToRecord(existing, fingerprint));
        var identity = Identity(existing, fingerprint);
        await lifecycle.AppendAsync(IssueLifecycleEvent.Create(
            identity,
            LifecycleStateName(state),
            "human",
            author,
            reason,
            now,
            expiresAt), cancellationToken).ConfigureAwait(false);
        var projected = Project(document, await lifecycle.ReadAsync(cancellationToken).ConfigureAwait(false));
        await SaveIfChangedAsync(document, projected, cancellationToken).ConfigureAwait(false);
        return ToLookup(projected)[fingerprint];
    }, cancellationToken).ConfigureAwait(false);

    public async Task<FindingStateRecord> ResolveAsync(
        string fingerprint,
        string actorKind,
        string actor,
        string reason,
        string? policyRef = null,
        IReadOnlyList<string>? basisObservationIds = null,
        DateTimeOffset? expectedTimestamp = null,
        CancellationToken cancellationToken = default) => await ExecuteLockedAsync(async () =>
    {
        if (actorKind is not ("human" or "policy"))
            throw new ArgumentException("Resolution actorKind must be human or policy.", nameof(actorKind));
        ValidateActor(actor, reason);
        if (actorKind == "policy" &&
            (string.IsNullOrWhiteSpace(policyRef) || basisObservationIds is not { Count: > 0 }))
            throw new ArgumentException("Policy-backed resolution requires a policyRef and basis observations.");
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLifecycleInitializedAsync(document, cancellationToken).ConfigureAwait(false);
        var states = await ReadWithExpiryAsync(cancellationToken).ConfigureAwait(false);
        var existing = FindByFingerprint(states, fingerprint)
            ?? throw new KeyNotFoundException($"Finding '{fingerprint}' was not found.");
        if (expectedTimestamp is not null && expectedTimestamp.Value != existing.Timestamp)
            throw new FindingStateConflictException(fingerprint, ToRecord(existing, fingerprint));
        await lifecycle.AppendAsync(IssueLifecycleEvent.Create(
            Identity(existing, fingerprint),
            CoreQualityTerms.Lifecycles.Resolved,
            actorKind,
            actor,
            reason,
            clock().ToUniversalTime(),
            policyRef: policyRef,
            basisObservationIds: basisObservationIds), cancellationToken).ConfigureAwait(false);
        var projected = Project(document, await lifecycle.ReadAsync(cancellationToken).ConfigureAwait(false));
        await SaveIfChangedAsync(document, projected, cancellationToken).ConfigureAwait(false);
        return ToLookup(projected)[fingerprint];
    }, cancellationToken).ConfigureAwait(false);

    private async Task EnsureLifecycleInitializedAsync(
        FindingStateDocument document,
        CancellationToken cancellationToken)
    {
        if (document.Findings.Count == 0) return;
        var existingEvents = await lifecycle.ReadEventsAsync(cancellationToken).ConfigureAwait(false);
        var importedFingerprints = existingEvents
            .SelectMany(value => value.LegacyFingerprints.Append(value.OccurrenceFingerprint))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var record in document.Findings.OrderBy(value => value.Timestamp))
        {
            if (importedFingerprints.Contains(record.Fingerprint)) continue;
            var identity = new FindingIdentityRecord(
                record.Fingerprint,
                record.FindingId,
                record.Path,
                record.RuleId,
                record.IssueId,
                record.OccurrenceFingerprint,
                record.FingerprintAlgorithm ?? FindingIdentity.Canonicalization,
                record.LegacyFingerprints ?? [record.Fingerprint]).WithV2Identity();
            await lifecycle.AppendAsync(IssueLifecycleEvent.Create(
                identity,
                LifecycleStateName(record.State),
                "imported",
                record.Author,
                record.Reason,
                record.Timestamp,
                record.ExpiresAt), cancellationToken).ConfigureAwait(false);
            importedFingerprints.Add(record.Fingerprint);
        }
    }

    private async Task<IReadOnlyDictionary<string, IssueLifecycleState>> ReadWithExpiryAsync(
        CancellationToken cancellationToken)
    {
        var states = await lifecycle.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = clock().ToUniversalTime();
        foreach (var state in states.Values.Where(value =>
                     value.ExpiresAt is not null && value.ExpiresAt <= now &&
                     value.State is not ("open" or "resolved")).ToArray())
        {
            await lifecycle.AppendAsync(IssueLifecycleEvent.Create(
                Identity(state, state.LegacyFingerprints.First()),
                CoreQualityTerms.Lifecycles.Open,
                "system",
                "quality-studio",
                $"{state.State} state expired.",
                now), cancellationToken).ConfigureAwait(false);
        }
        return await lifecycle.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FindingStateDocument Project(
        FindingStateDocument previous,
        IReadOnlyDictionary<string, IssueLifecycleState> states)
    {
        var records = states.Values
            .SelectMany(state => state.LegacyFingerprints.DefaultIfEmpty(state.OccurrenceFingerprint)
                .Select(alias => ToRecord(state, alias)))
            .GroupBy(record => record.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(record => record.Timestamp).First())
            .OrderBy(record => record.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var candidate = new FindingStateDocument(1, previous.Revision, records);
        return SnapshotEquivalent(previous, candidate)
            ? candidate
            : candidate with { Revision = previous.Revision + 1 };
    }

    private async Task<FindingStateDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath)) return new(1, 0, []);
        await using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var document = await JsonSerializer.DeserializeAsync<FindingStateDocument>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Finding state must be a JSON object.");
        if (document.SchemaVersion != 1)
            throw new JsonException($"Unsupported finding state schemaVersion '{document.SchemaVersion}'.");
        if (document.Revision < 0 || document.Findings is null)
            throw new JsonException("Finding state revision or findings is invalid.");
        if (document.Findings.GroupBy(record => record.Fingerprint, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new JsonException("Finding state contains duplicate fingerprints.");
        if (document.Findings.Any(record => !IsFingerprint(record.Fingerprint) ||
            string.IsNullOrWhiteSpace(record.FindingId) || string.IsNullOrWhiteSpace(record.Path) ||
            string.IsNullOrWhiteSpace(record.RuleId) || string.IsNullOrWhiteSpace(record.Author) ||
            string.IsNullOrWhiteSpace(record.Reason)))
            throw new JsonException("Finding state contains an invalid record.");
        return document;
    }

    private async Task SaveIfChangedAsync(
        FindingStateDocument previous,
        FindingStateDocument projected,
        CancellationToken cancellationToken)
    {
        if (!SnapshotEquivalent(previous, projected)) await SaveAsync(projected, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(FindingStateDocument document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, statePath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
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
                catch (IOException)
                {
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
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
        FindingState.FalsePositive => CoreQualityTerms.Lifecycles.FalsePositive,
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string LifecycleStateName(FindingState state) => state == FindingState.Accepted
        ? CoreQualityTerms.Lifecycles.AcceptedRisk
        : StateName(state);

    private static FindingState ParseState(string state) => state switch
    {
        "open" => FindingState.Open,
        "accepted" or "accepted-risk" => FindingState.Accepted,
        "waived" => FindingState.Waived,
        "falsePositive" or "false-positive" => FindingState.FalsePositive,
        "resolved" => FindingState.Resolved,
        _ => throw new JsonException($"Unsupported finding state '{state}'."),
    };

    private static IssueLifecycleState? FindByFingerprint(
        IReadOnlyDictionary<string, IssueLifecycleState> states,
        string fingerprint) => states.Values.FirstOrDefault(value =>
        string.Equals(value.OccurrenceFingerprint, fingerprint, StringComparison.Ordinal) ||
        value.LegacyFingerprints.Contains(fingerprint, StringComparer.Ordinal));

    private static FindingIdentityRecord Identity(IssueLifecycleState state, string fingerprint) => new(
        fingerprint,
        state.FindingId,
        state.Path,
        state.RuleId,
        state.IssueId,
        state.OccurrenceFingerprint,
        state.FingerprintAlgorithm,
        state.LegacyFingerprints);

    private static FindingStateRecord ToRecord(IssueLifecycleState state, string fingerprint) => new(
        fingerprint,
        state.FindingId,
        state.Path,
        state.RuleId,
        ParseState(state.State),
        state.Actor,
        state.Reason,
        state.Timestamp,
        state.ExpiresAt,
        state.IssueId,
        state.OccurrenceFingerprint,
        state.FingerprintAlgorithm,
        null);

    private static void ValidateActor(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("A state author is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A state reason is required.", nameof(reason));
        if (actor.Length > 200) throw new ArgumentException("A state author cannot exceed 200 characters.", nameof(actor));
        if (reason.Length > 2000) throw new ArgumentException("A state reason cannot exceed 2,000 characters.", nameof(reason));
    }

    private static bool SnapshotEquivalent(FindingStateDocument left, FindingStateDocument right)
    {
        if (left.SchemaVersion != right.SchemaVersion || left.Findings.Count != right.Findings.Count) return false;
        for (var index = 0; index < left.Findings.Count; index++)
        {
            var first = left.Findings[index];
            var second = right.Findings[index];
            if (first with { LegacyFingerprints = null } != second with { LegacyFingerprints = null } ||
                !(first.LegacyFingerprints ?? []).SequenceEqual(second.LegacyFingerprints ?? [], StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    private static bool IsFingerprint(string? value) => value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
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
            ParseState(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, FindingState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(StateName(value));
    }
}

public sealed class FindingStateConflictException(string fingerprint, FindingStateRecord current)
    : Exception($"Finding '{fingerprint}' changed after it was loaded.")
{
    public FindingStateRecord Current { get; } = current;
}
