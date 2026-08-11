using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record IssueLifecycleEvent(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string EventId,
    [property: JsonPropertyOrder(3)] string IssueId,
    [property: JsonPropertyOrder(4)] string OccurrenceFingerprint,
    [property: JsonPropertyOrder(5)] string FingerprintAlgorithm,
    [property: JsonPropertyOrder(6)] IReadOnlyList<string> LegacyFingerprints,
    [property: JsonPropertyOrder(7)] string FindingId,
    [property: JsonPropertyOrder(8)] string Path,
    [property: JsonPropertyOrder(9)] string RuleId,
    [property: JsonPropertyOrder(10)] string State,
    [property: JsonPropertyOrder(11)] string ActorKind,
    [property: JsonPropertyOrder(12)] string Actor,
    [property: JsonPropertyOrder(13)] string Reason,
    [property: JsonPropertyOrder(14)] DateTimeOffset Timestamp,
    [property: JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyOrder(16), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PolicyRef,
    [property: JsonPropertyOrder(17)] IReadOnlyList<string> BasisObservationIds,
    [property: JsonPropertyOrder(18), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, JsonElement>? Extensions = null)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-issue-lifecycle-event.v1.schema.json";

    public static IssueLifecycleEvent Create(
        FindingIdentityRecord finding,
        string state,
        string actorKind,
        string actor,
        string reason,
        DateTimeOffset timestamp,
        DateTimeOffset? expiresAt = null,
        string? policyRef = null,
        IReadOnlyList<string>? basisObservationIds = null)
    {
        var identity = finding.WithV2Identity();
        var basis = basisObservationIds?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                    ?? [];
        var canonical = new StringBuilder("quality-studio-lifecycle-event-v1\0")
            .Append(identity.IssueId).Append('\0')
            .Append(identity.OccurrenceFingerprint).Append('\0')
            .Append(state).Append('\0')
            .Append(actorKind).Append('\0')
            .Append(actor.Trim()).Append('\0')
            .Append(reason.Trim()).Append('\0')
            .Append(timestamp.ToUniversalTime().ToString("O")).Append('\0')
            .Append(expiresAt?.ToUniversalTime().ToString("O") ?? string.Empty).Append('\0')
            .Append(policyRef ?? string.Empty).Append('\0')
            .AppendJoin('\0', basis);
        var eventId = "lifecycle-sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        var value = new IssueLifecycleEvent(
            SchemaId,
            CurrentSchemaVersion,
            eventId,
            identity.IssueId!,
            identity.OccurrenceFingerprint!,
            identity.FingerprintAlgorithm,
            identity.LegacyFingerprints ?? [identity.Fingerprint],
            identity.Id,
            identity.Path,
            identity.RuleId,
            state,
            actorKind,
            actor.Trim(),
            reason.Trim(),
            timestamp.ToUniversalTime(),
            expiresAt?.ToUniversalTime(),
            policyRef,
            basis);
        Validate(value);
        return value;
    }

    public static void Validate(IssueLifecycleEvent value)
    {
        if (value.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(value.Schema, SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported lifecycle event schemaVersion '{value.SchemaVersion}'.");
        if (value.State is not ("open" or "accepted-risk" or "waived" or "false-positive" or "resolved"))
            throw new JsonException($"Unsupported lifecycle state '{value.State}'.");
        if (value.ActorKind is not ("agent" or "human" or "policy" or "imported" or "system" or "unknown"))
            throw new JsonException($"Unsupported lifecycle actor kind '{value.ActorKind}'.");
        if (string.IsNullOrWhiteSpace(value.Actor) || string.IsNullOrWhiteSpace(value.Reason))
            throw new JsonException("Lifecycle actor and reason are required.");
        if (value.State == "resolved" && value.ActorKind == "policy" &&
            (string.IsNullOrWhiteSpace(value.PolicyRef) || value.BasisObservationIds.Count == 0))
            throw new JsonException("Policy-backed resolution requires policyRef and basis observation ids.");
    }
}

public sealed record IssueLifecycleState(
    string IssueId,
    string State,
    string ActorKind,
    string Actor,
    string Reason,
    DateTimeOffset Timestamp,
    DateTimeOffset? ExpiresAt,
    string FindingId,
    string Path,
    string RuleId,
    string OccurrenceFingerprint,
    string FingerprintAlgorithm,
    IReadOnlyList<string> LegacyFingerprints,
    string? PolicyRef,
    IReadOnlyList<string> BasisObservationIds,
    string EventId);

public sealed class IssueLifecycleStore
{
    public const string RelativePath = ".quality/findings/lifecycle.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;

    public IssueLifecycleStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        path = System.IO.Path.Combine(System.IO.Path.GetFullPath(repositoryRoot),
            RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }

    public string Path => path;

    public async Task<bool> AppendAsync(
        IssueLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        IssueLifecycleEvent.Validate(lifecycleEvent);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && await ContainsAsync(lifecycleEvent.EventId, cancellationToken).ConfigureAwait(false))
                return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var line = JsonSerializer.Serialize(lifecycleEvent, LineOptions) + "\n";
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<IssueLifecycleEvent>> ReadEventsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return [];
        var result = new List<IssueLifecycleEvent>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var value = JsonSerializer.Deserialize<IssueLifecycleEvent>(line, LineOptions);
                if (value is null) continue;
                IssueLifecycleEvent.Validate(value);
                result.Add(value);
            }
            catch (JsonException)
            {
                // A malformed historical line must not hide later lifecycle events.
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IssueLifecycleState>> ReadAsync(
        CancellationToken cancellationToken = default) => Reduce(
        await ReadEventsAsync(cancellationToken).ConfigureAwait(false));

    public static IReadOnlyDictionary<string, IssueLifecycleState> Reduce(
        IEnumerable<IssueLifecycleEvent> events)
    {
        var states = new Dictionary<string, IssueLifecycleState>(StringComparer.Ordinal);
        foreach (var value in events.DistinctBy(item => item.EventId)
                     .OrderBy(item => item.Timestamp))
        {
            states[value.IssueId] = new IssueLifecycleState(
                value.IssueId,
                value.State,
                value.ActorKind,
                value.Actor,
                value.Reason,
                value.Timestamp,
                value.ExpiresAt,
                value.FindingId,
                value.Path,
                value.RuleId,
                value.OccurrenceFingerprint,
                value.FingerprintAlgorithm,
                value.LegacyFingerprints,
                value.PolicyRef,
                value.BasisObservationIds,
                value.EventId);
        }
        return states;
    }

    private async Task<bool> ContainsAsync(string eventId, CancellationToken cancellationToken)
    {
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("eventId", out var id) &&
                    string.Equals(id.GetString(), eventId, StringComparison.Ordinal)) return true;
            }
            catch (JsonException)
            {
                // Keep scanning after malformed lines.
            }
        }
        return false;
    }
}
