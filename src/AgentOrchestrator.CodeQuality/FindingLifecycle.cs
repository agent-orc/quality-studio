using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public enum FindingLifecycleEventKind
{
    Observed,
    StateChanged,
    AutomatedResolution,
    Reopened,
    ImportedSnapshot,
}

public sealed record FindingLifecycleEvent(
    int SchemaVersion,
    string EventId,
    string IssueId,
    string OccurrenceFingerprint,
    IReadOnlyList<string> FingerprintAliases,
    QualityLifecycleState State,
    FindingLifecycleEventKind Kind,
    string Author,
    string Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ExpiresAt = null,
    string? PolicyRef = null,
    IReadOnlyList<string>? BasisObservationIds = null);

public sealed class FindingLifecycleStore
{
    public const int CurrentSchemaVersion = 2;
    public const string RelativePath = ".quality/findings/lifecycle.v2.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string path;

    public FindingLifecycleStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        path = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(repositoryRoot),
            RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public string Path => path;

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
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<FindingLifecycleEvent>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        var events = new List<FindingLifecycleEvent>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var lifecycleEvent = JsonSerializer.Deserialize<FindingLifecycleEvent>(line, JsonOptions);
                if (lifecycleEvent is null) continue;
                Validate(lifecycleEvent);
                events.Add(lifecycleEvent);
            }
            catch (JsonException)
            {
                // Preserve append-only recovery: one partial line must not hide later events.
            }
        }
        return events;
    }

    public static string IssueId(string occurrenceFingerprint)
    {
        var canonical = "quality-studio-issue-v1\0" + occurrenceFingerprint;
        return "issue-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static FindingLifecycleEvent Create(
        string occurrenceFingerprint,
        QualityLifecycleState state,
        FindingLifecycleEventKind kind,
        string author,
        string reason,
        DateTimeOffset timestamp,
        DateTimeOffset? expiresAt = null,
        string? policyRef = null,
        IReadOnlyList<string>? basisObservationIds = null,
        IReadOnlyList<string>? fingerprintAliases = null)
    {
        var issueId = IssueId(occurrenceFingerprint);
        var canonical = string.Join('\0',
            "quality-studio-lifecycle-event-v2",
            issueId,
            state.ToString(),
            kind.ToString(),
            author.Trim(),
            reason.Trim(),
            timestamp.ToUniversalTime().ToString("O"),
            expiresAt?.ToUniversalTime().ToString("O") ?? string.Empty,
            policyRef ?? string.Empty,
            string.Join(',', basisObservationIds ?? []));
        var eventId = "lifecycle-sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new FindingLifecycleEvent(
            CurrentSchemaVersion,
            eventId,
            issueId,
            occurrenceFingerprint,
            fingerprintAliases ?? [occurrenceFingerprint],
            state,
            kind,
            author.Trim(),
            reason.Trim(),
            timestamp.ToUniversalTime(),
            expiresAt?.ToUniversalTime(),
            policyRef,
            basisObservationIds);
    }

    private async Task<bool> ContainsAsync(string eventId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("eventId", out var existing) &&
                    string.Equals(existing.GetString(), eventId, StringComparison.Ordinal)) return true;
            }
            catch (JsonException)
            {
                // Ignore incomplete historical lines during idempotency checks.
            }
        }
        return false;
    }

    private static void Validate(FindingLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.SchemaVersion != CurrentSchemaVersion ||
            !lifecycleEvent.EventId.StartsWith("lifecycle-sha256:", StringComparison.Ordinal) ||
            !lifecycleEvent.IssueId.StartsWith("issue-sha256:", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.OccurrenceFingerprint) ||
            lifecycleEvent.FingerprintAliases.Count == 0 ||
            string.IsNullOrWhiteSpace(lifecycleEvent.Author) ||
            string.IsNullOrWhiteSpace(lifecycleEvent.Reason) ||
            lifecycleEvent.Timestamp.Offset != TimeSpan.Zero)
            throw new JsonException("Finding lifecycle event is incomplete.");
        if (lifecycleEvent.Kind == FindingLifecycleEventKind.AutomatedResolution &&
            (lifecycleEvent.State != QualityLifecycleState.Resolved ||
             string.IsNullOrWhiteSpace(lifecycleEvent.PolicyRef) ||
             lifecycleEvent.BasisObservationIds is not { Count: > 0 }))
            throw new JsonException("Automated resolution requires a policyRef and basis observation ids.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
