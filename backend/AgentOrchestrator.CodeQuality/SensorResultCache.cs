using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// What a repository-wide sensor result depends on in the working copy: the commit it was taken at,
/// and the content of every file that differs from that commit. Two equal fingerprints mean the
/// sensor would read the same sources, so its earlier result can stand in for a new run.
/// <para>
/// Files Git ignores (dependencies, build output) are not part of it: a sensor whose answer changes
/// with <c>node_modules</c> alone is re-run with <c>refreshSensors</c>. Analyzer reports under
/// <c>.quality/preflight</c> are excluded too, because the sensors write them into the checkout and
/// would otherwise invalidate their own entry on every run.
/// </para>
/// </summary>
public sealed record SensorInputFingerprint(string Head, string Value, int DirtyFiles)
{
    internal const string PreflightDirectory = ".quality/preflight/";

    /// <summary>The fingerprint of <paramref name="repositoryRoot"/>, or null outside a Git checkout with a commit.</summary>
    public static async Task<SensorInputFingerprint?> ComputeAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) return null;
        string head;
        string topLevel;
        string status;
        try
        {
            head = (await GitPlumbing.RunAsync(root, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken)
                .ConfigureAwait(false)).Trim();
            topLevel = Path.GetFullPath((await GitPlumbing.RunAsync(root, ["rev-parse", "--show-toplevel"],
                cancellationToken).ConfigureAwait(false)).Trim());
            // Porcelain paths are relative to the top level; the pathspec keeps edits outside the
            // registered root (a sibling project in the same repository) from invalidating it.
            status = await GitPlumbing.RunAsync(root,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ChangeReviewException or IOException)
        {
            return null;
        }
        if (head.Length == 0) return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, head);
        var dirty = 0;
        foreach (var path in GitStatusPaths.Parse(status)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (IsSensorOutput(path)) continue;
            dirty++;
            Append(hash, path);
            var absolute = Path.GetFullPath(Path.Combine(topLevel, path));
            Append(hash, File.Exists(absolute)
                ? await ContentHashAsync(absolute, cancellationToken).ConfigureAwait(false)
                : "deleted");
        }

        return new SensorInputFingerprint(head, "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), dirty);
    }

    private static bool IsSensorOutput(string path) =>
        path.StartsWith(PreflightDirectory, StringComparison.Ordinal) ||
        path.Contains("/" + PreflightDirectory, StringComparison.Ordinal);

    private static async Task<string> ContentHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81920, useAsync: true);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that vanished or is locked mid-hash still has to change the fingerprint,
            // never be skipped as if it were clean.
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }
}

/// <summary>How one sensor contributed to a run: served from the cache, executed, or left out.</summary>
public static class SensorOutcome
{
    public const string Cached = "cached";
    public const string Ran = "ran";
    public const string OptInRequired = "opt-in-required";
}

/// <summary>What one sensor did for one run, as the run reports it.</summary>
public sealed record SensorExecutionRecord(string SensorId, string Outcome, long DurationMs, string? Detail = null);

/// <summary>
/// Repository-wide sensor results kept per commit and inputs, so a review run reuses the evidence of
/// the previous run on an unchanged working copy instead of executing every enabled sensor again.
/// <para>
/// One instance serves one run: it takes the working-copy fingerprint once, answers each sensor
/// from its entry when the fingerprint, the sensor version and the sensor configuration all match,
/// and records a fresh result only when the working copy did not move while the sensor ran.
/// Unavailable results are never stored - a missing tool may be installed before the next run.
/// </para>
/// </summary>
public sealed class SensorResultCache
{
    public const int SchemaVersion = 1;

    /// <summary>The folder below the project's data root that holds the entries.</summary>
    public const string DirectoryName = "sensor-cache";

    /// <summary>How many entries each sensor keeps; older commits are pruned on write.</summary>
    public const int EntriesPerSensor = 8;

    private const string ObservedFileName = "observed.json";
    private static readonly JsonSerializerOptions JsonOptions = new(ReviewMetaJson.Options) { WriteIndented = false };
    private readonly string repositoryRoot;
    private readonly bool refresh;
    private readonly List<SensorExecutionRecord> executions = [];

    private SensorResultCache(string repositoryRoot, SensorInputFingerprint? fingerprint, bool refresh)
    {
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        Fingerprint = fingerprint;
        this.refresh = refresh;
    }

    /// <summary>The working copy this run's sensors see; null when results cannot be keyed and are never reused.</summary>
    public SensorInputFingerprint? Fingerprint { get; }

    /// <summary>What each sensor did through this instance, in completion order.</summary>
    public IReadOnlyList<SensorExecutionRecord> Executions
    {
        get { lock (executions) return executions.ToArray(); }
    }

    /// <param name="refresh">Ignore stored entries and run every sensor; fresh results are still stored.</param>
    public static async Task<SensorResultCache> OpenAsync(
        string repositoryRoot,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var fingerprint = await SensorInputFingerprint.ComputeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return new SensorResultCache(repositoryRoot, fingerprint, refresh);
    }

    /// <summary>The stored result for this sensor and configuration, without running anything.</summary>
    public SensorResultCacheEntry? TryGet(IReviewSensor sensor, IReadOnlyDictionary<string, string>? configuration)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        if (Fingerprint is null || refresh) return null;
        var path = EntryPath(sensor.Id, Key(sensor, configuration, Fingerprint));
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<SensorResultCacheEntry>(File.ReadAllText(path), JsonOptions);
            return entry is { SchemaVersion: SchemaVersion } &&
                   string.Equals(entry.SensorId, sensor.Id, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(entry.SensorVersion, sensor.Version, StringComparison.Ordinal) &&
                   string.Equals(entry.Fingerprint, Fingerprint.Value, StringComparison.Ordinal) &&
                   entry.Result is not null
                ? entry
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged entry is a miss, never a run failure; the fresh result overwrites it.
            return null;
        }
    }

    /// <summary>
    /// The stored result when one matches, otherwise the sensor's fresh result, stored for the next run.
    /// </summary>
    public async Task<SensorScanResult> GetOrRunAsync(
        IReviewSensor sensor,
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(request);
        if (TryGet(sensor, request.Configuration) is { } cached)
        {
            Record(new SensorExecutionRecord(sensor.Id, SensorOutcome.Cached, 0,
                $"stored {cached.StoredAt:O} at {Short(cached.Head)}"));
            return cached.Result;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await sensor.RunAsync(request, cancellationToken).ConfigureAwait(false);
        var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Record(new SensorExecutionRecord(sensor.Id, SensorOutcome.Ran, elapsed,
            Fingerprint is null ? "not cached: no Git commit to key the result to" : null));
        await TryStoreObservedAsync(sensor.Id, elapsed).ConfigureAwait(false);
        if (Fingerprint is null || !result.Available) return result;

        // A working copy that moved while the sensor ran produced a result for neither state.
        var after = await SensorInputFingerprint.ComputeAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (after is null || !string.Equals(after.Value, Fingerprint.Value, StringComparison.Ordinal)) return result;

        var entry = new SensorResultCacheEntry(SchemaVersion, sensor.Id, sensor.Version, Fingerprint.Head,
            Fingerprint.Value, DateTimeOffset.UtcNow, elapsed, result);
        try
        {
            var directory = SensorDirectory(sensor.Id);
            await AtomicFile.WriteAllTextAsync(
                EntryPath(sensor.Id, Key(sensor, request.Configuration, Fingerprint)),
                JsonSerializer.Serialize(entry, JsonOptions), CancellationToken.None).ConfigureAwait(false);
            Prune(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run has its evidence; a cache that cannot be written only costs the next run time.
        }
        return result;
    }

    /// <summary>How long this sensor took the last time it actually ran in this repository, if it ever did.</summary>
    public static long? LastObservedDurationMs(string repositoryRoot, string sensorId)
    {
        var path = Path.Combine(QualityDataRoot.Combine(repositoryRoot, DirectoryName), SafeSegment(sensorId), ObservedFileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ObservedDuration>(File.ReadAllText(path), JsonOptions)?.DurationMs;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The entry key: everything besides the working copy that decides the result - the sensor, its
    /// version, and its repository configuration in a canonical order.
    /// </summary>
    internal static string Key(
        IReviewSensor sensor,
        IReadOnlyDictionary<string, string>? configuration,
        SensorInputFingerprint fingerprint)
    {
        var builder = new StringBuilder()
            .Append(SchemaVersion).Append('\0')
            .Append(sensor.Id.ToLowerInvariant()).Append('\0')
            .Append(sensor.Version).Append('\0')
            .Append(fingerprint.Value).Append('\0');
        foreach (var pair in (configuration ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private void Record(SensorExecutionRecord record)
    {
        lock (executions) executions.Add(record);
    }

    private async Task TryStoreObservedAsync(string sensorId, long elapsed)
    {
        try
        {
            await AtomicFile.WriteAllTextAsync(
                Path.Combine(SensorDirectory(sensorId), ObservedFileName),
                JsonSerializer.Serialize(new ObservedDuration(elapsed, DateTimeOffset.UtcNow), JsonOptions),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the launcher's duration hint depends on it.
        }
    }

    private string SensorDirectory(string sensorId) =>
        Path.Combine(QualityDataRoot.Combine(repositoryRoot, DirectoryName), SafeSegment(sensorId));

    private string EntryPath(string sensorId, string key) =>
        Path.Combine(SensorDirectory(sensorId), key + ".json");

    private static void Prune(string directory)
    {
        var stale = new DirectoryInfo(directory).EnumerateFiles("*.json")
            .Where(file => !string.Equals(file.Name, ObservedFileName, StringComparison.Ordinal))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(EntriesPerSensor);
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    // Sensor ids come from the host's registry, but a path segment is still confined on principle.
    private static string SafeSegment(string sensorId)
    {
        var builder = new StringBuilder(sensorId.Length);
        foreach (var character in sensorId.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        return builder.Length == 0 ? "_" : builder.ToString();
    }

    private static string Short(string head) => head.Length > 12 ? head[..12] : head;

    private sealed record ObservedDuration(
        [property: JsonPropertyName("durationMs")] long DurationMs,
        [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt);
}

/// <summary>One stored sensor result and the working copy it was taken from.</summary>
public sealed record SensorResultCacheEntry(
    int SchemaVersion,
    string SensorId,
    string SensorVersion,
    string Head,
    string Fingerprint,
    DateTimeOffset StoredAt,
    long DurationMs,
    SensorScanResult Result);
