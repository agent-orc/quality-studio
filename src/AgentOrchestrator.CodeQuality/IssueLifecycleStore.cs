using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record IssueLifecycleEvent(
    int SchemaVersion,
    string EventId,
    string IssueId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    IReadOnlyList<string> FingerprintAliases,
    string State,
    string ProducerKind,
    string Author,
    string Reason,
    DateTimeOffset OccurredAt,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<string>? BasisObservationIds = null,
    string? PolicyRef = null);

public sealed record IssueLifecycleProjection(
    string IssueId,
    string OccurrenceFingerprint,
    IReadOnlyList<string> FingerprintAliases,
    string State,
    string Author,
    string Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ExpiresAt,
    string EventId);

/// <summary>Append-only lifecycle authority. The v1 state file is a compatibility projection.</summary>
public static class IssueLifecycleStore
{
    public const int CurrentSchemaVersion = 1;
    public const string RelativePath = ".quality/findings/lifecycle.v2.jsonl";
    private static readonly HashSet<string> States =
        ["open", "accepted-risk", "waived", "false-positive", "resolved"];
    private static readonly HashSet<string> ProducerKinds =
        ["agent", "deterministic-sensor", "human", "imported", "unknown"];
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetPath(string repositoryRoot) => Path.Combine(
        Path.GetFullPath(repositoryRoot),
        RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static IssueLifecycleEvent CreateEvent(
        string issueId,
        string occurrenceFingerprint,
        IReadOnlyList<string> fingerprintAliases,
        string state,
        string producerKind,
        string author,
        string reason,
        DateTimeOffset occurredAt,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? basisObservationIds = null,
        string? policyRef = null)
    {
        if (!States.Contains(state)) throw new ArgumentException($"Unsupported lifecycle state '{state}'.", nameof(state));
        if (!ProducerKinds.Contains(producerKind))
            throw new ArgumentException($"Unsupported lifecycle producer kind '{producerKind}'.", nameof(producerKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(issueId);
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (occurredAt.Offset != TimeSpan.Zero) throw new ArgumentException("Lifecycle timestamps must be UTC.", nameof(occurredAt));
        if (expiresAt is not null && expiresAt.Value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Lifecycle expiry must be UTC.", nameof(expiresAt));
        if (state == "resolved" &&
            (basisObservationIds is not { Count: > 0 } || string.IsNullOrWhiteSpace(policyRef)))
            throw new ArgumentException("Resolution requires basis observation ids and a reconciliation policy.", nameof(state));

        var aliases = fingerprintAliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var basis = basisObservationIds?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var canonical = string.Join('\0',
            "quality-studio-lifecycle-event-v1",
            issueId,
            occurrenceFingerprint,
            string.Join(',', aliases),
            state,
            producerKind,
            author.Trim(),
            reason.Trim(),
            occurredAt.ToString("O"),
            expiresAt?.ToString("O") ?? string.Empty,
            basis is null ? string.Empty : string.Join(',', basis),
            policyRef ?? string.Empty);
        var eventId = "lifecycle-sha256:" +
                      Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new IssueLifecycleEvent(
            CurrentSchemaVersion,
            eventId,
            issueId,
            occurrenceFingerprint,
            FindingIdentity.OccurrenceCanonicalization,
            aliases,
            state,
            producerKind,
            author.Trim(),
            reason.Trim(),
            occurredAt,
            expiresAt,
            basis,
            policyRef);
    }

    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        IssueLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default)
    {
        Validate(lifecycleEvent);
        var path = GetPath(repositoryRoot);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        if (document.RootElement.TryGetProperty("eventId", out var eventId) &&
                            string.Equals(eventId.GetString(), lifecycleEvent.EventId, StringComparison.Ordinal))
                            return false;
                    }
                    catch (JsonException)
                    {
                        // A malformed historical event must not hide later valid lifecycle history.
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lifecycleEvent, JsonOptions) + "\n");
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<IssueLifecycleEvent>> ReadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(repositoryRoot);
        if (!File.Exists(path)) return [];
        var events = new List<IssueLifecycleEvent>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var lifecycleEvent = JsonSerializer.Deserialize<IssueLifecycleEvent>(line, JsonOptions);
                if (lifecycleEvent is not null)
                {
                    Validate(lifecycleEvent);
                    events.Add(lifecycleEvent);
                }
            }
            catch (JsonException)
            {
                // A malformed historical event must not hide later valid lifecycle history.
            }
        }
        return events;
    }

    public static IReadOnlyDictionary<string, IssueLifecycleProjection> Reduce(
        IEnumerable<IssueLifecycleEvent> events,
        DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("Projection time must be UTC.", nameof(now));
        var result = new Dictionary<string, IssueLifecycleProjection>(StringComparer.Ordinal);
        foreach (var group in events.Select((item, index) => (Item: item, Index: index))
                     .GroupBy(item => item.Item.IssueId, StringComparer.Ordinal))
        {
            var latest = group.OrderBy(item => item.Item.OccurredAt).ThenBy(item => item.Index).Last().Item;
            var expired = latest.ExpiresAt is not null && latest.ExpiresAt <= now &&
                          latest.State is not ("open" or "resolved");
            result[group.Key] = new IssueLifecycleProjection(
                latest.IssueId,
                latest.OccurrenceFingerprint,
                latest.FingerprintAliases,
                expired ? "open" : latest.State,
                expired ? "quality-studio" : latest.Author,
                expired ? $"{latest.State} state expired." : latest.Reason,
                expired ? now : latest.OccurredAt,
                expired ? null : latest.ExpiresAt,
                latest.EventId);
        }
        return result;
    }

    private static void Validate(IssueLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"Unsupported lifecycle schemaVersion '{lifecycleEvent.SchemaVersion}'.");
        if (!States.Contains(lifecycleEvent.State) || !ProducerKinds.Contains(lifecycleEvent.ProducerKind) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.EventId) || string.IsNullOrWhiteSpace(lifecycleEvent.IssueId) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.OccurrenceFingerprint) || string.IsNullOrWhiteSpace(lifecycleEvent.Author) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.Reason))
            throw new JsonException("Lifecycle event is invalid.");
        if (lifecycleEvent.State == "resolved" &&
            (lifecycleEvent.BasisObservationIds is not { Count: > 0 } || string.IsNullOrWhiteSpace(lifecycleEvent.PolicyRef)))
            throw new JsonException("Resolution event requires basis observation ids and a reconciliation policy.");
    }
}
