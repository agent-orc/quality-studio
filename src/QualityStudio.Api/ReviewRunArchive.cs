using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QualityStudio.Api;

public sealed record RunArchiveTarget(string UnitId, string Name, string Path, string SubjectHash);

public sealed record RunArchiveEstimate(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    int HistorySamples,
    string Method);

/// <summary>Immutable identity, plan and configuration for one archived review run. Create-only.</summary>
public sealed record RunArchiveRecord(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string RunId,
    [property: JsonPropertyOrder(3)] string RepositoryId,
    [property: JsonPropertyOrder(4)] DateTimeOffset CreatedAt,
    [property: JsonPropertyOrder(5)] string Kind,
    [property: JsonPropertyOrder(6)] string Level,
    [property: JsonPropertyOrder(7)] string ScopeUnitId,
    [property: JsonPropertyOrder(8)] string Path,
    [property: JsonPropertyOrder(9)] IReadOnlyList<RunArchiveTarget> Targets,
    [property: JsonPropertyOrder(10)] string? Model,
    [property: JsonPropertyOrder(11)] string ThinkingLevel,
    [property: JsonPropertyOrder(12)] string CliType,
    [property: JsonPropertyOrder(13)] bool Force,
    [property: JsonPropertyOrder(14)] long? TokenCap,
    [property: JsonPropertyOrder(15)] decimal? CostCap,
    [property: JsonPropertyOrder(16)] RunArchiveEstimate? Estimate,
    [property: JsonPropertyOrder(17)] string? SourceRevision,
    [property: JsonPropertyOrder(18)] bool? SourceDirty);

public sealed record RunArchiveGrade(int Score, string Band);

/// <summary>What one operation observed. Appended only after its result is durably captured.</summary>
public sealed record RunArchiveOperation(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string OperationId,
    [property: JsonPropertyOrder(3)] int Ordinal,
    [property: JsonPropertyOrder(4)] int Attempt,
    [property: JsonPropertyOrder(5)] string UnitId,
    [property: JsonPropertyOrder(6)] string Level,
    [property: JsonPropertyOrder(7)] string Path,
    [property: JsonPropertyOrder(8)] string State,
    [property: JsonPropertyOrder(9)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(10)] DateTimeOffset? FinishedAt,
    [property: JsonPropertyOrder(11)] string? ProviderRunId,
    [property: JsonPropertyOrder(12)] string? ReviewedHash,
    [property: JsonPropertyOrder(13)] string? SidecarPath,
    [property: JsonPropertyOrder(14)] string? SidecarSha256,
    [property: JsonPropertyOrder(15)] RunArchiveGrade? Grade,
    [property: JsonPropertyOrder(16)] string? SecurityVerdict,
    [property: JsonPropertyOrder(17)] DateTimeOffset ObservedAt);

public sealed record RunArchiveFindingLocation(string Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

/// <summary>A finding as observed by one operation. Never becomes the current lifecycle owner.</summary>
public sealed record RunArchiveFinding(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string OperationId,
    [property: JsonPropertyOrder(3)] string Fingerprint,
    [property: JsonPropertyOrder(4)] string FindingId,
    [property: JsonPropertyOrder(5)] string RuleId,
    [property: JsonPropertyOrder(6)] string Severity,
    [property: JsonPropertyOrder(7)] string Title,
    [property: JsonPropertyOrder(8)] IReadOnlyList<RunArchiveFindingLocation> Locations,
    [property: JsonPropertyOrder(9)] string State,
    [property: JsonPropertyOrder(10)] DateTimeOffset ObservedAt);

public sealed record RunArchiveCounts(int Reviewed, int ReusedFresh, int Failed, int Skipped, int Cancelled);

public sealed record RunArchiveUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus);

public sealed record RunArchiveCap(long? TokenLimit, decimal? CostLimit, string Outcome, string? Reason);

public sealed record RunArchiveEstimateDeviation(decimal? InputTokensPercent, decimal? OutputTokensPercent, decimal? CostPercent);

public sealed record RunArchiveAttemptSummary(
    int? LowestGradeScore,
    string? LowestGradeBand,
    string? WorstSecurityVerdict,
    int ActiveFindingCount,
    string? HighestActiveSeverity);

/// <summary>One stopped attempt of a run. Create-only; a capped run that resumes gets another attempt, never a rewrite.</summary>
public sealed record RunArchiveAttempt(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] int Attempt,
    [property: JsonPropertyOrder(3)] string Outcome,
    [property: JsonPropertyOrder(4)] string Completeness,
    [property: JsonPropertyOrder(5)] DateTimeOffset StartedAt,
    [property: JsonPropertyOrder(6)] DateTimeOffset? FinishedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset ArchivedAt,
    [property: JsonPropertyOrder(8)] RunArchiveCounts Counts,
    [property: JsonPropertyOrder(9)] RunArchiveCounts CumulativeCounts,
    [property: JsonPropertyOrder(10)] RunArchiveUsage Usage,
    [property: JsonPropertyOrder(11)] IReadOnlyList<string> ErrorCodes,
    [property: JsonPropertyOrder(12)] RunArchiveCap Cap,
    [property: JsonPropertyOrder(13)] RunArchiveEstimateDeviation? EstimateDeviation,
    [property: JsonPropertyOrder(14)] IReadOnlyList<string> UsageLedgerMonths,
    [property: JsonPropertyOrder(15)] RunArchiveAttemptSummary Summary);

/// <summary>Canonical JSON contract for the four run-history v1 schemas.</summary>
public static class ReviewRunArchiveJson
{
    public const string RunRecordSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";
    public const string OperationSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";
    public const string FindingSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";
    public const string AttemptSchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    public static JsonSerializerOptions DocumentOptions { get; } = CreateOptions(indented: true);
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(indented: false);

    public static string SerializeRun(RunArchiveRecord record)
    {
        ValidateSchema(record.Schema, RunRecordSchemaId, record.SchemaVersion);
        return JsonSerializer.Serialize(record, DocumentOptions) + Environment.NewLine;
    }

    public static RunArchiveRecord DeserializeRun(string json)
    {
        var record = JsonSerializer.Deserialize<RunArchiveRecord>(json, DocumentOptions)
            ?? throw new JsonException("A run archive record must be a JSON object.");
        ValidateSchema(record.Schema, RunRecordSchemaId, record.SchemaVersion);
        return record;
    }

    public static string SerializeOperationLine(RunArchiveOperation operation)
    {
        ValidateSchema(operation.Schema, OperationSchemaId, operation.SchemaVersion);
        return JsonSerializer.Serialize(operation, LineOptions);
    }

    public static RunArchiveOperation DeserializeOperationLine(string json)
    {
        var operation = JsonSerializer.Deserialize<RunArchiveOperation>(json, LineOptions)
            ?? throw new JsonException("A run archive operation must be a JSON object.");
        ValidateSchema(operation.Schema, OperationSchemaId, operation.SchemaVersion);
        return operation;
    }

    public static string SerializeFindingLine(RunArchiveFinding finding)
    {
        ValidateSchema(finding.Schema, FindingSchemaId, finding.SchemaVersion);
        return JsonSerializer.Serialize(finding, LineOptions);
    }

    public static RunArchiveFinding DeserializeFindingLine(string json)
    {
        var finding = JsonSerializer.Deserialize<RunArchiveFinding>(json, LineOptions)
            ?? throw new JsonException("A run archive finding must be a JSON object.");
        ValidateSchema(finding.Schema, FindingSchemaId, finding.SchemaVersion);
        return finding;
    }

    public static string SerializeAttempt(RunArchiveAttempt attempt)
    {
        ValidateSchema(attempt.Schema, AttemptSchemaId, attempt.SchemaVersion);
        return JsonSerializer.Serialize(attempt, DocumentOptions) + Environment.NewLine;
    }

    public static RunArchiveAttempt DeserializeAttempt(string json)
    {
        var attempt = JsonSerializer.Deserialize<RunArchiveAttempt>(json, DocumentOptions)
            ?? throw new JsonException("A run archive attempt must be a JSON object.");
        ValidateSchema(attempt.Schema, AttemptSchemaId, attempt.SchemaVersion);
        return attempt;
    }

    private static void ValidateSchema(string schema, string expectedSchema, int schemaVersion)
    {
        if (schemaVersion != 1 || !string.Equals(schema, expectedSchema, StringComparison.Ordinal))
            throw new JsonException($"Unsupported run archive schema. Expected '{expectedSchema}' at version 1.");
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>
/// Backend-owned, tracked archive of stopped review runs at <c>.quality/run-history/YYYY-MM/&lt;runId&gt;/</c>.
/// Distinct from <see cref="ReviewRunStore"/>, which stays the ignored, mutable crash-recovery journal.
/// The application never rewrites or deletes archived files; callers append or create new attempt numbers.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string archivePath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archivePath = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchivePath => archivePath;

    /// <summary>Creates the immutable run record. Throws <see cref="IOException"/> if the run already exists.</summary>
    public void CreateRun(RunArchiveRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var month = MonthOf(record.CreatedAt);
        var directory = RunDirectory(month, record.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), ReviewRunArchiveJson.SerializeRun(record));
    }

    public void AppendOperation(string runId, RunArchiveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = RequireRunDirectory(runId);
        AppendLine(Path.Combine(directory, "operations.jsonl"), ReviewRunArchiveJson.SerializeOperationLine(operation));
    }

    public void AppendFinding(string runId, RunArchiveFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = RequireRunDirectory(runId);
        AppendLine(Path.Combine(directory, "findings.jsonl"), ReviewRunArchiveJson.SerializeFindingLine(finding));
    }

    /// <summary>Creates the next attempt snapshot. Throws <see cref="IOException"/> if that attempt number already exists.</summary>
    public void CreateAttempt(string runId, RunArchiveAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1) throw new ArgumentException("An attempt number must be positive.", nameof(attempt));
        var directory = RequireRunDirectory(runId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var fileName = attempt.Attempt.ToString("0000", CultureInfo.InvariantCulture) + ".json";
        WriteCreateOnly(Path.Combine(attemptsDirectory, fileName), ReviewRunArchiveJson.SerializeAttempt(attempt));
    }

    public RunArchiveRecord LoadRun(string runId)
    {
        var directory = RequireRunDirectory(runId);
        return ReviewRunArchiveJson.DeserializeRun(File.ReadAllText(Path.Combine(directory, "run.json")));
    }

    public IReadOnlyList<RunArchiveOperation> LoadOperations(string runId) =>
        ReadLines(Path.Combine(FindRunDirectory(runId) ?? throw RunNotFound(runId), "operations.jsonl"),
            ReviewRunArchiveJson.DeserializeOperationLine);

    public IReadOnlyList<RunArchiveFinding> LoadFindings(string runId) =>
        ReadLines(Path.Combine(FindRunDirectory(runId) ?? throw RunNotFound(runId), "findings.jsonl"),
            ReviewRunArchiveJson.DeserializeFindingLine);

    /// <summary>Loads every stopped attempt for the run, ordered by attempt number. A corrupt attempt file throws.</summary>
    public IReadOnlyList<RunArchiveAttempt> LoadAttempts(string runId)
    {
        var directory = RequireRunDirectory(runId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        return Directory.EnumerateFiles(attemptsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path => ReviewRunArchiveJson.DeserializeAttempt(File.ReadAllText(path)))
            .OrderBy(attempt => attempt.Attempt)
            .ToArray();
    }

    public bool RunExists(string runId) => FindRunDirectory(runId) is not null;

    private string RequireRunDirectory(string runId) => FindRunDirectory(runId) ?? throw RunNotFound(runId);

    private static InvalidOperationException RunNotFound(string runId) =>
        new($"Run archive '{runId}' does not exist.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archivePath)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archivePath).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private string RunDirectory(string month, string runId)
    {
        ValidateMonth(month);
        ValidateRunId(runId);
        return Path.Combine(archivePath, month, runId);
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A run id cannot contain path separators.", nameof(runId));
    }

    private static void ValidateMonth(string month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(month);
        if (month.Length != 7 || month[4] != '-' ||
            !int.TryParse(month.AsSpan(0, 4), out _) || !int.TryParse(month.AsSpan(5, 2), out _) ||
            !string.Equals(month, Path.GetFileName(month), StringComparison.Ordinal))
            throw new ArgumentException("A run archive month must be in 'yyyy-MM' form.", nameof(month));
    }

    private static string MonthOf(DateTimeOffset createdAt) => createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static IReadOnlyList<T> ReadLines<T>(string path, Func<string, T> parse)
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                items.Add(parse(line));
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return items;
    }
}
