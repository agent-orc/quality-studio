using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Immutable identity, plan, and configuration of one logical review run.</summary>
public sealed record ReviewRunArchiveRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string RepositoryId,
    [property: JsonPropertyOrder(4)] string Kind,
    [property: JsonPropertyOrder(5)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(6)] DateTimeOffset ArchivedAt,
    [property: JsonPropertyOrder(7)] ReviewRunArchiveSubject Subject,
    [property: JsonPropertyOrder(8)] IReadOnlyList<ReviewRunArchiveTarget> Targets,
    [property: JsonPropertyOrder(9)] string ManifestHash,
    [property: JsonPropertyOrder(10)] ReviewRunArchiveConfiguration Configuration,
    [property: JsonPropertyOrder(11)] ReviewRunArchiveCap Cap,
    [property: JsonPropertyOrder(12)] ReviewRunArchiveEstimate? Estimate,
    [property: JsonPropertyOrder(13)] ReviewRunArchiveRevision? Revision,
    [property: JsonPropertyOrder(14)] string Provenance);

public sealed record ReviewRunArchiveSubject(string UnitId, string Name, string Path, string Level);

public sealed record ReviewRunArchiveTarget(string UnitId, string Name, string Path, string SubjectHash);

public sealed record ReviewRunArchiveConfiguration(
    string Model,
    string ThinkingLevel,
    string CliType,
    bool Force,
    bool RouteOverride);

public sealed record ReviewRunArchiveCap(long? TokenLimit, decimal? CostLimit);

public sealed record ReviewRunArchiveEstimate(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    string PriceStatus,
    int HistorySamples,
    string Method);

/// <summary>Source revision the run observed, with an explicit dirty-worktree marker.</summary>
public sealed record ReviewRunArchiveRevision(string? Commit, bool Dirty);

/// <summary>
/// One completed or failed agent operation. The verdict keeps its producer's semantics; an
/// operational state of <c>done</c> never implies a quality pass.
/// </summary>
public sealed record ReviewRunOperationRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] int Ordinal,
    [property: JsonPropertyOrder(5)] int Attempt,
    [property: JsonPropertyOrder(6)] string UnitId,
    [property: JsonPropertyOrder(7)] string Path,
    [property: JsonPropertyOrder(8)] string Level,
    [property: JsonPropertyOrder(9)] string State,
    [property: JsonPropertyOrder(10)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(11)] DateTimeOffset? FinishedAt,
    [property: JsonPropertyOrder(12)] string? ProviderRunId,
    [property: JsonPropertyOrder(13)] string? ReviewedHash,
    [property: JsonPropertyOrder(14)] string? ReviewInputsHash,
    [property: JsonPropertyOrder(15)] string? SidecarPath,
    [property: JsonPropertyOrder(16)] string? SidecarSha256,
    [property: JsonPropertyOrder(17)] ReviewRunArchiveVerdict? Verdict,
    [property: JsonPropertyOrder(18)] string? Error);

/// <summary>
/// A typed quality verdict. <paramref name="Type"/> names the producing review family so a
/// security verdict is never coerced into a numeric grade or a universal pass/fail.
/// </summary>
public sealed record ReviewRunArchiveVerdict(string Type, string Value, int? Score, string? Band);

/// <summary>What one operation observed for a stable finding fingerprint at observation time.</summary>
public sealed record ReviewRunFindingRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string OperationId,
    [property: JsonPropertyOrder(4)] string Fingerprint,
    [property: JsonPropertyOrder(5)] string FindingId,
    [property: JsonPropertyOrder(6)] string RuleId,
    [property: JsonPropertyOrder(7)] string Severity,
    [property: JsonPropertyOrder(8)] string Title,
    [property: JsonPropertyOrder(9)] IReadOnlyList<QualityFindingLocation> Locations,
    [property: JsonPropertyOrder(10)] string State,
    [property: JsonPropertyOrder(11)] DateTimeOffset ObservedAt);

/// <summary>
/// One stopped attempt of a logical run. A capped attempt is a stop, not the end of the run: a
/// later resume adds the next attempt and never rewrites this record.
/// </summary>
public sealed record ReviewRunAttemptRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] int Attempt,
    [property: JsonPropertyOrder(4)] string Outcome,
    [property: JsonPropertyOrder(5)] string Completeness,
    [property: JsonPropertyOrder(6)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset? FinishedAt,
    [property: JsonPropertyOrder(8)] DateTimeOffset ArchivedAt,
    [property: JsonPropertyOrder(9)] ReviewRunArchiveCounters Counters,
    [property: JsonPropertyOrder(10)] ReviewRunArchiveSpend Spend,
    [property: JsonPropertyOrder(11)] IReadOnlyList<string> Errors,
    [property: JsonPropertyOrder(12)] string? StopReason,
    [property: JsonPropertyOrder(13)] ReviewRunArchiveCap Cap,
    [property: JsonPropertyOrder(14)] ReviewRunArchiveDeviation? EstimateDeviation,
    [property: JsonPropertyOrder(15)] IReadOnlyList<string> LedgerMonths,
    [property: JsonPropertyOrder(16)] IReadOnlyList<string> OperationIds,
    [property: JsonPropertyOrder(17)] ReviewRunArchiveQuality Quality);

public sealed record ReviewRunArchiveCounters(
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    int Cancelled);

public sealed record ReviewRunArchiveSpend(
    int Operations,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record ReviewRunArchiveDeviation(
    decimal? InputTokensPercent,
    decimal? OutputTokensPercent,
    decimal? CostPercent);

/// <summary>
/// Deterministic rollup of the attempt's observations. It never states a repository-wide
/// pass threshold; report gates stay explicit through their own options.
/// </summary>
public sealed record ReviewRunArchiveQuality(
    int? Score,
    string? Grade,
    string? WorstSecurityVerdict,
    int ActiveFindings,
    string? HighestSeverity);

public sealed record StoredReviewRunArchive(
    ReviewRunArchiveRecord Run,
    IReadOnlyList<ReviewRunOperationRecord> Operations,
    IReadOnlyList<ReviewRunFindingRecord> Findings,
    IReadOnlyList<ReviewRunAttemptRecord> Attempts)
{
    /// <summary>The highest valid attempt number is the latest state of the logical run.</summary>
    public ReviewRunAttemptRecord? LatestAttempt =>
        Attempts.Count == 0 ? null : Attempts[^1];
}

/// <summary>Typed archive failure. Corrupt tracked history is surfaced, never silently omitted.</summary>
public sealed class ReviewRunArchiveException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public const string HistoryCorrupt = "history-corrupt";

    public string Code { get; } = code;
}

/// <summary>Identity of one operation inside a run: its stable id, its plan ordinal, and its key.</summary>
public readonly record struct ReviewOperationIdentity(string OperationId, int Ordinal, string OperationKey);

/// <summary>Stable operation identity, generated before agent execution and reused after recovery.</summary>
public static class ReviewOperationId
{
    /// <summary>Operation key of the container review that follows the file operations.</summary>
    public const string AggregateKey = QualityRunReportFactory.AggregateOperationId;

    /// <summary>
    /// Derives the operation id from the run id and the operation key so recovery of the same
    /// operation reproduces the same identity without an extra persisted allocation table.
    /// </summary>
    public static string For(string runId, string operationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        var canonical = Encoding.UTF8.GetBytes($"quality-studio-run-operation-v1\n{runId}\0{operationKey}");
        return "op-" + Convert.ToHexStringLower(SHA256.HashData(canonical))[..24];
    }
}

public static class ReviewRunArchiveJson
{
    public const string RunSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string OperationSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string FindingSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string AttemptSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    private static readonly string[] OperationStates =
        ["done", "failed", "cancelled", "skipped", "skipped-fresh"];
    private static readonly string[] AttemptOutcomes = ["done", "failed", "cancelled", "capped"];

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static JsonSerializerOptions LineOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(ReviewRunArchiveRecord run) =>
        JsonSerializer.Serialize(Validate(run), Options) + Environment.NewLine;

    public static string Serialize(ReviewRunAttemptRecord attempt) =>
        JsonSerializer.Serialize(Validate(attempt), Options) + Environment.NewLine;

    public static string SerializeLine(ReviewRunOperationRecord operation) =>
        JsonSerializer.Serialize(Validate(operation), LineOptions);

    public static string SerializeLine(ReviewRunFindingRecord finding) =>
        JsonSerializer.Serialize(Validate(finding), LineOptions);

    public static ReviewRunArchiveRecord Validate(ReviewRunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        RequireSchema(run.Schema, RunSchemaId, run.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RepositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.ManifestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.Provenance);
        ArgumentNullException.ThrowIfNull(run.Targets);
        return run;
    }

    public static ReviewRunOperationRecord Validate(ReviewRunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireSchema(operation.Schema, OperationSchemaId, operation.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);
        if (operation.Ordinal < 0)
            throw new ArgumentException("A run operation ordinal cannot be negative.", nameof(operation));
        if (operation.Attempt < 1)
            throw new ArgumentException("A run operation attempt starts at one.", nameof(operation));
        if (!OperationStates.Contains(operation.State, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported run operation state '{operation.State}'.", nameof(operation));
        return operation;
    }

    public static ReviewRunFindingRecord Validate(ReviewRunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        RequireSchema(finding.Schema, FindingSchemaId, finding.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(finding.Fingerprint);
        ArgumentNullException.ThrowIfNull(finding.Locations);
        return finding;
    }

    public static ReviewRunAttemptRecord Validate(ReviewRunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        RequireSchema(attempt.Schema, AttemptSchemaId, attempt.SchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(attempt.RunId);
        if (attempt.Attempt < 1)
            throw new ArgumentException("A run attempt number starts at one.", nameof(attempt));
        if (!AttemptOutcomes.Contains(attempt.Outcome, StringComparer.Ordinal))
            throw new ArgumentException($"Unsupported run attempt outcome '{attempt.Outcome}'.", nameof(attempt));
        if (attempt.Completeness is not ("complete" or "partial"))
            throw new ArgumentException("A run attempt completeness must be complete or partial.", nameof(attempt));
        return attempt;
    }

    private static void RequireSchema(string schema, string expected, int schemaVersion)
    {
        if (schemaVersion != 1 || !string.Equals(schema, expected, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported review run archive schema '{schema}' v{schemaVersion}.");
    }
}

/// <summary>
/// Tracked, immutable archive of terminal review-run truth under
/// <c>.quality/run-history/YYYY-MM/&lt;runId&gt;/</c>. It never replaces the ignored
/// <see cref="ReviewRunStore"/> recovery journal and never touches Git: the backend only writes
/// files, and the surrounding workflow makes them durable.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    public const string ProvenanceLive = "live-dual-write";
    public const string ProvenanceMigrated = "migrated-from-run-store-v0";

    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string archivePath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archivePath = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchivePath => archivePath;

    public static string MonthSegment(DateTimeOffset createdAt) =>
        createdAt.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Directory the run would occupy, whether or not it has been created yet.</summary>
    public string RunDirectory(string runId, DateTimeOffset createdAt) =>
        Confine(Path.Combine(archivePath, MonthSegment(createdAt), SafeSegment(runId)));

    /// <summary>Creates the immutable run record. Throws when the run is already archived.</summary>
    public void CreateRun(ReviewRunArchiveRecord run)
    {
        ReviewRunArchiveJson.Validate(run);
        var directory = RunDirectory(run.RunId, run.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), ReviewRunArchiveJson.Serialize(run));
    }

    /// <summary>
    /// Create-only archive creation for repeatable callers such as migration. Returns
    /// <see langword="false"/> when the run record already exists, leaving it untouched.
    /// </summary>
    public bool TryCreateRun(ReviewRunArchiveRecord run)
    {
        try
        {
            CreateRun(run);
            return true;
        }
        catch (IOException) when (File.Exists(Path.Combine(RunDirectory(run.RunId, run.CreatedAt), "run.json")))
        {
            return false;
        }
    }

    public void AppendOperation(ReviewRunOperationRecord operation) =>
        AppendLine(operation.RunId, "operations.jsonl", ReviewRunArchiveJson.SerializeLine(operation));

    public void AppendFinding(ReviewRunFindingRecord finding) =>
        AppendLine(finding.RunId, "findings.jsonl", ReviewRunArchiveJson.SerializeLine(finding));

    /// <summary>Next create-only attempt number for the run; the first stopped attempt is 1.</summary>
    public int NextAttemptNumber(string runId)
    {
        var attempts = Path.Combine(RequireRunDirectory(runId), "attempts");
        if (!Directory.Exists(attempts)) return 1;
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(attempts, "*.json", SearchOption.TopDirectoryOnly))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) && number > highest)
                highest = number;
        }
        return highest + 1;
    }

    /// <summary>Writes one stopped attempt. An existing attempt number is never rewritten.</summary>
    public void CreateAttempt(ReviewRunAttemptRecord attempt)
    {
        ReviewRunArchiveJson.Validate(attempt);
        var directory = Path.Combine(RequireRunDirectory(attempt.RunId), "attempts");
        Directory.CreateDirectory(directory);
        var name = attempt.Attempt.ToString("0000", CultureInfo.InvariantCulture) + ".json";
        WriteCreateOnly(Path.Combine(directory, name), ReviewRunArchiveJson.Serialize(attempt));
    }

    public bool Exists(string runId) => TryLocateRunDirectory(runId, out _);

    public StoredReviewRunArchive Load(string runId) => Read(RequireRunDirectory(runId));

    public bool TryLoad(string runId, out StoredReviewRunArchive? archive)
    {
        if (!TryLocateRunDirectory(runId, out var directory))
        {
            archive = null;
            return false;
        }
        archive = Read(directory!);
        return true;
    }

    /// <summary>
    /// Reads every archived run, newest month first. A corrupt archive is reported through
    /// <paramref name="loadFailed"/> with a typed <see cref="ReviewRunArchiveException"/>.
    /// </summary>
    public IReadOnlyList<StoredReviewRunArchive> LoadAll(Action<string, ReviewRunArchiveException>? loadFailed = null)
    {
        var archives = new List<StoredReviewRunArchive>();
        foreach (var directory in EnumerateRunDirectories())
        {
            try
            {
                archives.Add(Read(directory));
            }
            catch (ReviewRunArchiveException exception)
            {
                loadFailed?.Invoke(directory, exception);
            }
        }
        return archives;
    }

    private IEnumerable<string> EnumerateRunDirectories()
    {
        if (!Directory.Exists(archivePath)) yield break;
        var months = Directory.EnumerateDirectories(archivePath)
            .Where(month => IsMonthSegment(Path.GetFileName(month)))
            .OrderDescending(StringComparer.Ordinal)
            .ToArray();
        foreach (var month in months)
        {
            foreach (var run in Directory.EnumerateDirectories(month).Order(StringComparer.Ordinal))
                yield return run;
        }
    }

    private StoredReviewRunArchive Read(string directory)
    {
        var runPath = Path.Combine(directory, "run.json");
        var run = ReadDocument<ReviewRunArchiveRecord>(runPath);
        if (!string.Equals(run.RunId, Path.GetFileName(directory), StringComparison.Ordinal))
            throw Corrupt(runPath, "the archived run id does not match its directory", null);
        var attempts = ReadAttempts(directory);
        return new StoredReviewRunArchive(
            run,
            ReadLines<ReviewRunOperationRecord>(Path.Combine(directory, "operations.jsonl")),
            ReadLines<ReviewRunFindingRecord>(Path.Combine(directory, "findings.jsonl")),
            attempts);
    }

    private IReadOnlyList<ReviewRunAttemptRecord> ReadAttempts(string directory)
    {
        var attemptDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptDirectory)) return [];
        var attempts = new List<ReviewRunAttemptRecord>();
        foreach (var file in Directory.EnumerateFiles(attemptDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var attempt = ReadDocument<ReviewRunAttemptRecord>(file);
            var name = attempt.Attempt.ToString("0000", CultureInfo.InvariantCulture);
            if (!string.Equals(name, Path.GetFileNameWithoutExtension(file), StringComparison.Ordinal))
                throw Corrupt(file, "the attempt number does not match its file name", null);
            attempts.Add(attempt);
        }
        return attempts;
    }

    private static T ReadDocument<T>(string path) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReviewRunArchiveJson.Options)
                   ?? throw Corrupt(path, "the archived document is empty", null);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Corrupt(path, "the archived document could not be read", exception);
        }
    }

    private static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                records.Add(JsonSerializer.Deserialize<T>(line, ReviewRunArchiveJson.LineOptions)
                            ?? throw Corrupt(path, $"line {number} is empty", null));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Archive lines are appended after the corresponding result is already durable, so a
                // malformed line is real corruption rather than an expected torn crash write.
                throw Corrupt(path, $"line {number} could not be read", exception);
            }
        }
        return records;
    }

    private static ReviewRunArchiveException Corrupt(string path, string reason, Exception? inner) =>
        new(ReviewRunArchiveException.HistoryCorrupt,
            $"Review run history is corrupt: {reason} in '{path}'.", inner);

    private void AppendLine(string runId, string fileName, string line)
    {
        var path = Path.Combine(RequireRunDirectory(runId), fileName);
        var bytes = Utf8.GetBytes(line + "\n");
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
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
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private string RequireRunDirectory(string runId) =>
        TryLocateRunDirectory(runId, out var directory)
            ? directory!
            : throw new ReviewRunArchiveException("history-missing",
                $"Review run '{runId}' has no archive under '{archivePath}'.");

    private bool TryLocateRunDirectory(string runId, out string? directory)
    {
        directory = null;
        var name = SafeSegment(runId);
        if (!Directory.Exists(archivePath)) return false;
        foreach (var month in Directory.EnumerateDirectories(archivePath).Order(StringComparer.Ordinal))
        {
            if (!IsMonthSegment(Path.GetFileName(month))) continue;
            var candidate = Confine(Path.Combine(month, name));
            if (!File.Exists(Path.Combine(candidate, "run.json"))) continue;
            directory = candidate;
            return true;
        }
        return false;
    }

    private string Confine(string candidate)
    {
        var full = Path.GetFullPath(candidate);
        if (!PathConfinement.IsWithin(archivePath, full, allowRoot: false))
            throw new ArgumentException("A review run archive path escapes the run-history root.");
        return full;
    }

    private static bool IsMonthSegment(string? segment) =>
        segment is { Length: 7 } && segment[4] == '-' &&
        segment.Where((character, index) => index != 4).All(char.IsAsciiDigit);

    private static string SafeSegment(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > 200 || runId.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException(
                "A review run id may contain only letters, digits, dots, underscores, and hyphens.", nameof(runId));
        return runId;
    }

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
