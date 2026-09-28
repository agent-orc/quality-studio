using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>The latest attempt of one sensor, kept even when it could not produce a result.</summary>
public sealed record AnalyzerAttempt(bool Available, string? UnavailableReason, string ScannedAt, string Scope, string Target);

/// <summary>
/// The persisted state of one deterministic sensor for one working copy: the latest available
/// result, and the latest attempt when that attempt was unavailable.
/// </summary>
public sealed record AnalyzerResultDocument(
    int SchemaVersion,
    string SensorId,
    SensorScanResult? Result,
    AnalyzerAttempt LastAttempt);

/// <summary>An analyzer finding for one file, with the named catalogue rules its rule id enforces.</summary>
public sealed record AnalyzerFileFinding(
    string SensorId,
    ReviewFinding Finding,
    IReadOnlyList<CatalogueRuleLink> CatalogueRules);

/// <summary>What the persisted analyzers say about one sensor, without its findings.</summary>
public sealed record AnalyzerSensorSummary(
    string SensorId,
    bool Available,
    string? UnavailableReason,
    string? ScannedAt,
    string? Scope,
    string? Target,
    int Findings,
    int SuppressedFindings,
    IReadOnlyDictionary<string, string> ToolVersions,
    AnalyzerAttempt LastAttempt);

/// <summary>The persisted analyzer findings of one file, and the state of every persisted sensor.</summary>
public sealed record AnalyzerFileView(
    IReadOnlyList<AnalyzerSensorSummary> Sensors,
    IReadOnlyList<AnalyzerFileFinding> Findings);

/// <summary>
/// Persists deterministic sensor results (Roslyn, ESLint, tsc, SARIF, compiler and architecture
/// checks) under the data root, <c>analyzers/&lt;sensor&gt;.json</c>, so the explorer and the editor show
/// them after the scan that produced them rather than only in that scan's HTTP response.
/// <para>
/// A repository-scoped scan replaces the sensor's findings. A path-scoped scan replaces only the
/// findings located under that path and keeps the rest, so scanning one folder never erases the
/// evidence of another. An unavailable scan keeps the previous result and records the attempt, so
/// a missing <c>npm ci</c> is visible without discarding what the analyzer last proved.
/// </para>
/// </summary>
public sealed class AnalyzerResultStore
{
    public const string DirectoryName = "analyzers";
    public const int SchemaVersion = 1;
    private const long MaximumDocumentBytes = 64L * 1024 * 1024;

    // The finding contract of the review sidecars, so a persisted finding reads back unchanged.
    private static JsonSerializerOptions JsonOptions => ReviewMetaJson.Options;

    // One entry per sensor document of each analysed working copy; cleared wholesale at the bound.
    private const int MaximumCachedDocuments = 256;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, ((DateTime Written, long Length) Stamp, AnalyzerResultDocument? Document)> Cache = new();

    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private readonly string repositoryRoot;

    public AnalyzerResultStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    public string Directory => QualityDataRoot.Combine(repositoryRoot, DirectoryName);

    /// <summary>Records one scan. Returns the document as persisted.</summary>
    public async Task<AnalyzerResultDocument> RecordAsync(SensorScanResult scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        var sensorId = scan.Provenance.SensorId;
        var path = PathFor(sensorId);
        await WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await ReadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            var attempt = new AnalyzerAttempt(
                scan.Available, scan.UnavailableReason, scan.Provenance.ScannedAt, scan.Provenance.Scope, scan.Provenance.Target);
            var result = scan.Available ? Merge(previous?.Result, scan) : previous?.Result;
            var document = new AnalyzerResultDocument(SchemaVersion, sensorId, result, attempt);
            System.IO.Directory.CreateDirectory(Directory);
            await AtomicFile.WriteAllTextAsync(
                path, JsonSerializer.Serialize(document, JsonOptions), cancellationToken).ConfigureAwait(false);
            return document;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>Every persisted sensor document, ordered by sensor id. Unreadable documents are skipped.</summary>
    public async Task<IReadOnlyList<AnalyzerResultDocument>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var documents = new List<AnalyzerResultDocument>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json").Order(StringComparer.Ordinal))
        {
            var document = await ReadDocumentAsync(file, cancellationToken).ConfigureAwait(false);
            if (document is not null) documents.Add(document);
        }
        return documents.OrderBy(document => document.SensorId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The persisted findings located in <paramref name="relativePath"/> (a file, or a directory and
    /// everything below it; <c>.</c> is the whole repository), each linked to the catalogue rules its
    /// rule id enforces.
    /// </summary>
    public async Task<AnalyzerFileView> ReadForPathAsync(
        string relativePath,
        DeterministicRuleMap ruleMap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleMap);
        var documents = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var normalized = Normalize(relativePath);
        var findings = documents
            .Where(document => document.Result is not null)
            .SelectMany(document => document.Result!.Findings
                .Where(finding => finding.Locations.Any(location => IsUnder(Normalize(location.Path), normalized)))
                .Select(finding => new AnalyzerFileFinding(document.SensorId, finding, ruleMap.For(finding.RuleId))))
            .OrderBy(finding => finding.Finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(finding => finding.Finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
            .ThenBy(finding => finding.SensorId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Finding.RuleId, StringComparer.Ordinal)
            .ToArray();
        return new AnalyzerFileView(documents.Select(Summarize).ToArray(), findings);
    }

    /// <summary>
    /// Per-file finding counts of every persisted sensor, keyed by repository-relative path. The
    /// explorer adds them up per folder. Suppressed-by-disposition filtering is not applied here: a
    /// deterministic finding is fixed at source or suppressed at source.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> CountByFileAsync(CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in await ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (document.Result is null) continue;
            foreach (var finding in document.Result.Findings)
                foreach (var path in finding.Locations.Select(location => Normalize(location.Path)).Distinct(StringComparer.Ordinal))
                {
                    if (path is "." or "" || path.StartsWith("external/", StringComparison.Ordinal)) continue;
                    counts[path] = counts.GetValueOrDefault(path) + 1;
                }
        }
        return counts;
    }

    private static AnalyzerSensorSummary Summarize(AnalyzerResultDocument document) => new(
        document.SensorId,
        document.Result is not null,
        document.LastAttempt.Available ? null : document.LastAttempt.UnavailableReason,
        document.Result?.Provenance.ScannedAt,
        document.Result?.Provenance.Scope,
        document.Result?.Provenance.Target,
        document.Result?.Findings.Count ?? 0,
        document.Result?.SuppressedFindings ?? 0,
        document.Result?.Provenance.ToolVersions ?? new Dictionary<string, string>(StringComparer.Ordinal),
        document.LastAttempt);

    private static SensorScanResult Merge(SensorScanResult? previous, SensorScanResult scan)
    {
        if (previous is null || scan.Provenance.Scope != "path") return scan;
        var scanned = Normalize(scan.Provenance.Target);
        // Findings the path scan could have reported again are replaced by what it did report.
        var kept = previous.Findings
            .Where(finding => !finding.Locations.Any(location => IsUnder(Normalize(location.Path), scanned)))
            .ToArray();
        return previous with
        {
            Findings = kept.Concat(scan.Findings)
                .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .OrderBy(finding => finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(finding => finding.Locations.FirstOrDefault()?.Range?.Start.Line ?? 0)
                .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                .ToArray(),
            // The newest scan's suppressions cover only its path; the count stays a lower bound.
            SuppressedFindings = Math.Max(previous.SuppressedFindings, scan.SuppressedFindings),
            Provenance = previous.Provenance with { ScannedAt = scan.Provenance.ScannedAt },
        };
    }

    private string PathFor(string sensorId)
    {
        if (string.IsNullOrWhiteSpace(sensorId) ||
            !sensorId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ||
            sensorId.StartsWith('.'))
            throw new ArgumentException($"Sensor id '{sensorId}' cannot name an analyzer result file.");
        return Path.Combine(Directory, sensorId.ToLowerInvariant() + ".json");
    }

    private static async Task<AnalyzerResultDocument?> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        try
        {
            if (info.Length > MaximumDocumentBytes) return null;
            // Every opened file reads every sensor's document; parse each version of a document once.
            var stamp = (info.LastWriteTimeUtc, info.Length);
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Document;
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<AnalyzerResultDocument>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            document = document is { SchemaVersion: SchemaVersion, LastAttempt: not null } ? document : null;
            if (Cache.Count >= MaximumCachedDocuments) Cache.Clear();
            Cache[path] = (stamp, document);
            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            // A torn or foreign file must not take the explorer down; the next scan rewrites it.
            return null;
        }
    }

    private static string Normalize(string? path)
    {
        var normalized = DeterministicEvidenceProjection.NormalizePath(path ?? ".").TrimEnd('/');
        return normalized.Length == 0 ? "." : normalized;
    }

    private static bool IsUnder(string path, string scope) =>
        scope == "." ||
        string.Equals(path, scope, StringComparison.Ordinal) ||
        path.StartsWith(scope + "/", StringComparison.Ordinal);
}
