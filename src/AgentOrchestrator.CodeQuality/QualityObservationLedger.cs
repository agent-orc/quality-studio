using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Rollout gates for the taxonomy migration. Writing observations is enabled first; reading them
/// as the authoritative projection is enabled separately, after a full comparison sweep.
/// </summary>
public sealed record QualityTaxonomyOptions(
    bool ObservationWriteEnabled = false,
    bool ObservationReadEnabled = false)
{
    public const string ObservationWriteKey = "QualityTaxonomy:ObservationWriteEnabled";
    public const string ObservationReadKey = "QualityTaxonomy:ObservationReadEnabled";

    public static QualityTaxonomyOptions Disabled { get; } = new();

    /// <summary>
    /// Reads the two gates from the environment using the standard .NET spelling of their
    /// configuration keys, so a host that binds configuration and a bare CLI agree.
    /// </summary>
    public static QualityTaxonomyOptions FromEnvironment(Func<string, string?>? read = null)
    {
        var lookup = read ?? Environment.GetEnvironmentVariable;
        return new(IsEnabled(lookup(EnvironmentName(ObservationWriteKey))),
            IsEnabled(lookup(EnvironmentName(ObservationReadKey))));
    }

    public static string EnvironmentName(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    private static bool IsEnabled(string? value) =>
        bool.TryParse(value, out var enabled) && enabled;
}

/// <summary>Deterministic identity of one observation, so a replayed run recomputes the same id.</summary>
public static class ObservationIdentity
{
    public const string Prefix = "observation-sha256:";

    /// <summary>
    /// Derives the identity from the exact facts that make a producer run unique: its run id, the
    /// subject it looked at, the inputs it was given, and the term meanings it was recorded under.
    /// </summary>
    public static string Compute(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string reviewInputsHash,
        string taxonomyDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewInputsHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(taxonomyDigest);
        var material = string.Join('\u001f', runId, unitId, kind, subjectHash, reviewInputsHash, taxonomyDigest);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// Append-only, repository-local store of immutable observations. A rerun with another model adds
/// a record; it never replaces one. Appending the same observation twice is a no-op, and a
/// partial or unsupported historical line never hides the rest of the ledger.
/// </summary>
public static class QualityObservationLedger
{
    public const string RelativeDirectory = ".quality/observations";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    /// <summary>
    /// Appends one observation. Returns <c>false</c> when the exact observation id is already
    /// stored, so replaying a run does not duplicate a line.
    /// </summary>
    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var line = QualityObservationJson.Serialize(observation, indented: false);
        var path = GetLedgerPath(repositoryRoot, observation.RecordedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(path, observation.ObservationId, cancellationToken).ConfigureAwait(false))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads every stored record, newest month last. Unsupported and malformed lines are returned
    /// quarantined with their raw text instead of being dropped or reinterpreted.
    /// </summary>
    public static async Task<IReadOnlyList<QualityObservationRecord>> ReadAsync(
        string repositoryRoot,
        QualityTaxonomyResolver? resolver = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return [];
        var records = new List<QualityObservationRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                records.Add(QualityObservationJson.Read(line, resolver));
            }
        }

        return records;
    }

    private static async Task<bool> ContainsAsync(string path, string observationId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (ReadObservationId(line) is { } stored &&
                string.Equals(stored, observationId, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? ReadObservationId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("observationId", out var id)
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            // A partial or corrupt historical line must not hide the rest of the append-only ledger.
            return null;
        }
    }
}

/// <summary>Raised when a review's authoritative observation could not be appended.</summary>
public sealed class QualityObservationAppendException(string message, Exception innerException)
    : Exception(message, innerException);
