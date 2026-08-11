using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingLifecycleEvent(
    int SchemaVersion,
    string EventId,
    string IssueId,
    IReadOnlyList<string> FingerprintAliases,
    string FindingId,
    string Path,
    string RuleRef,
    QualityLifecycleState State,
    string Actor,
    string Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? BasisObservationIds = null,
    string? PolicyRef = null);

public sealed record FindingLifecycleProjection(
    IReadOnlyDictionary<string, FindingLifecycleEvent> ByIssueId,
    int MalformedLines);

/// <summary>
/// Append-only issue lifecycle. Observations can open or reopen an issue, but only an
/// explicit human transition or a named reconciliation policy can settle it.
/// </summary>
public sealed class FindingLifecycleStore
{
    public const string RelativePath = ".quality/findings/lifecycle.v2.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string repositoryRoot;
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;

    public FindingLifecycleStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = System.IO.Path.GetFullPath(repositoryRoot);
        path = System.IO.Path.Combine(this.repositoryRoot,
            RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Path => path;

    public async Task ObserveAsync(
        QualityObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var current = (await ReadAsync(observation.ObservedAt, cancellationToken).ConfigureAwait(false)).ByIssueId;
        foreach (var finding in observation.Findings)
        {
            if (current.TryGetValue(finding.IssueId, out var state) &&
                EffectiveState(state, observation.ObservedAt) != QualityLifecycleState.Resolved) continue;
            var location = observation.Evidence
                .Where(item => finding.EvidenceRefs.Contains(item.Id, StringComparer.Ordinal))
                .Select(item => item.Locator)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Path));
            var aliases = new[] { finding.OccurrenceFingerprint }
                .Concat(finding.FingerprintAliases)
                .Distinct(StringComparer.Ordinal).ToArray();
            var lifecycleEvent = new FindingLifecycleEvent(
                2,
                "lifecycle-observe:" + observation.ObservationId["observation-".Length..] + ":" + finding.ObservationFindingId,
                finding.IssueId,
                aliases,
                finding.ObservationFindingId,
                location?.Path ?? ".",
                finding.RuleRef,
                QualityLifecycleState.Open,
                finding.Source.Kind == QualityProducerKind.Agent
                    ? observation.Producer.Agent
                    : finding.Source.ProducerRef,
                state is null ? "First observed." : "Issue reappeared in an observation.",
                observation.ObservedAt,
                BasisObservationIds: [observation.ObservationId]);
            await AppendAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
        }
        await WriteCompatibilitySnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FindingLifecycleEvent> TransitionAsync(
        string issueId,
        QualityLifecycleState state,
        string actor,
        string reason,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? basisObservationIds = null,
        string? policyRef = null,
        string? eventId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("A lifecycle actor is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A lifecycle reason is required.", nameof(reason));
        var projection = await ReadAsync(clock().ToUniversalTime(), cancellationToken).ConfigureAwait(false);
        if (!projection.ByIssueId.TryGetValue(issueId, out var current))
            throw new KeyNotFoundException($"Issue '{issueId}' was not found.");
        if (state == QualityLifecycleState.Resolved &&
            (string.IsNullOrWhiteSpace(policyRef) || basisObservationIds is not { Count: > 0 }))
            throw new ArgumentException("Resolution requires policyRef and basis observation ids.", nameof(state));
        var now = clock().ToUniversalTime();
        if (expiresAt is not null && expiresAt <= now)
            throw new ArgumentException("Lifecycle expiry must be in the future.", nameof(expiresAt));
        var next = current with
        {
            EventId = eventId ?? "lifecycle-sha256:" + QualityObservationJson.Hash(
                $"{issueId}\0{state}\0{actor}\0{reason}\0{now:O}")["sha256:".Length..],
            State = state,
            Actor = actor.Trim(),
            Reason = reason.Trim(),
            Timestamp = now,
            ExpiresAt = expiresAt?.ToUniversalTime(),
            BasisObservationIds = basisObservationIds,
            PolicyRef = policyRef,
        };
        await AppendAsync(next, cancellationToken).ConfigureAwait(false);
        await WriteCompatibilitySnapshotAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    public async Task<bool> AppendAsync(
        FindingLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default)
    {
        Validate(lifecycleEvent);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(lifecycleEvent.EventId, cancellationToken).ConfigureAwait(false)) return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lifecycleEvent, JsonOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n')
                {
                    stream.Position = stream.Length;
                    stream.WriteByte((byte)'\n');
                }
            }
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<FindingLifecycleProjection> ReadAsync(
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        var byIssue = new Dictionary<string, FindingLifecycleEvent>(StringComparer.Ordinal);
        var malformed = 0;
        if (!File.Exists(path)) return new(byIssue, malformed);
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var lifecycleEvent = JsonSerializer.Deserialize<FindingLifecycleEvent>(line, JsonOptions)
                    ?? throw new JsonException("Lifecycle event is null.");
                Validate(lifecycleEvent);
                if (byIssue.TryGetValue(lifecycleEvent.IssueId, out var prior))
                {
                    lifecycleEvent = lifecycleEvent with
                    {
                        FingerprintAliases = prior.FingerprintAliases.Concat(lifecycleEvent.FingerprintAliases)
                            .Distinct(StringComparer.Ordinal).ToArray(),
                    };
                }
                byIssue[lifecycleEvent.IssueId] = lifecycleEvent;
            }
            catch (JsonException)
            {
                malformed++;
            }
        }
        var effectiveAt = (at ?? clock()).ToUniversalTime();
        foreach (var (issueId, lifecycleEvent) in byIssue.ToArray())
        {
            if (EffectiveState(lifecycleEvent, effectiveAt) == lifecycleEvent.State) continue;
            byIssue[issueId] = lifecycleEvent with
            {
                State = QualityLifecycleState.Open,
                Actor = "quality-studio",
                Reason = $"{StateName(lifecycleEvent.State)} state expired.",
                Timestamp = effectiveAt,
                ExpiresAt = null,
            };
        }
        return new(byIssue, malformed);
    }

    public async Task WriteCompatibilitySnapshotAsync(CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projection = await ReadAsync(clock().ToUniversalTime(), cancellationToken).ConfigureAwait(false);
            var records = projection.ByIssueId.Values
                .SelectMany(lifecycleEvent => lifecycleEvent.FingerprintAliases.Select(fingerprint => new FindingStateRecord(
                    fingerprint,
                    lifecycleEvent.FindingId,
                    lifecycleEvent.Path,
                    lifecycleEvent.RuleRef,
                    LegacyState(lifecycleEvent.State),
                    lifecycleEvent.Actor,
                    lifecycleEvent.Reason,
                    lifecycleEvent.Timestamp,
                    lifecycleEvent.ExpiresAt)))
                .GroupBy(record => record.Fingerprint, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(record => record.Fingerprint, StringComparer.Ordinal)
                .ToArray();
            var statePath = System.IO.Path.Combine(repositoryRoot,
                FindingStateStore.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
            var root = new
            {
                schemaVersion = 1,
                revision = projection.ByIssueId.Count,
                findings = records.Select(record => new
                {
                    record.Fingerprint,
                    record.FindingId,
                    record.Path,
                    record.RuleId,
                    state = FindingStateStore.StateName(record.State),
                    record.Author,
                    record.Reason,
                    record.Timestamp,
                    record.ExpiresAt,
                }),
            };
            var temporary = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary,
                    JsonSerializer.Serialize(root, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    }) + Environment.NewLine,
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, statePath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ContainsAsync(string eventId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("eventId", out var id) && id.GetString() == eventId) return true;
            }
            catch (JsonException)
            {
                // Preserve and pass malformed historical lines.
            }
        }
        return false;
    }

    private static QualityLifecycleState EffectiveState(FindingLifecycleEvent lifecycleEvent, DateTimeOffset at) =>
        lifecycleEvent.ExpiresAt is not null && lifecycleEvent.ExpiresAt <= at &&
        lifecycleEvent.State is not (QualityLifecycleState.Open or QualityLifecycleState.Resolved)
            ? QualityLifecycleState.Open
            : lifecycleEvent.State;

    private static FindingState LegacyState(QualityLifecycleState state) => state switch
    {
        QualityLifecycleState.AcceptedRisk => FindingState.Accepted,
        QualityLifecycleState.Waived => FindingState.Waived,
        QualityLifecycleState.FalsePositive => FindingState.FalsePositive,
        QualityLifecycleState.Resolved => FindingState.Resolved,
        _ => FindingState.Open,
    };

    private static string StateName(QualityLifecycleState state) =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(state.ToString());

    private static void Validate(FindingLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(lifecycleEvent.EventId) ||
            !lifecycleEvent.IssueId.StartsWith("issue-sha256:", StringComparison.Ordinal) ||
            lifecycleEvent.FingerprintAliases is not { Count: > 0 } ||
            lifecycleEvent.FingerprintAliases.Any(value => !value.StartsWith("sha256:", StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.Actor) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.Reason))
            throw new JsonException("Finding lifecycle event is incomplete.");
        if (lifecycleEvent.State == QualityLifecycleState.Resolved &&
            (string.IsNullOrWhiteSpace(lifecycleEvent.PolicyRef) ||
             lifecycleEvent.BasisObservationIds is not { Count: > 0 }))
            throw new JsonException("A resolution event requires policyRef and basis observation ids.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
