using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record RunArchiveNode(string Id, string Path);

public sealed record RunArchiveTarget(string Path, string SubjectHash);

public sealed record RunArchiveConfiguration(string? Model, string CliType, string? ThinkingLevel, bool Force);

public sealed record RunArchiveCap(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TokenCap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostCap = null);

public sealed record RunArchiveEstimate(
    int Operations,
    long InputTokens,
    long OutputTokens,
    string PriceStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Cost = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null);

public sealed record RunArchiveSourceRevision(
    bool Dirty,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitSha = null);

/// <summary>Immutable identity, plan and configuration for one archived run. Written create-only.</summary>
public sealed record RunRecord(
    string RunId,
    string RepositoryId,
    RunArchiveNode Node,
    string Level,
    string Kind,
    DateTimeOffset CreatedAt,
    IReadOnlyList<RunArchiveTarget> Targets,
    RunArchiveConfiguration Configuration,
    RunArchiveSourceRevision SourceRevision,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunArchiveCap? Cap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunArchiveEstimate? Estimate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PromptHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveInputHash = null,
    int SchemaVersion = RunRecord.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;
}

public sealed record RunOperationVerdict(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GradeSnapshot? Grade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SecurityVerdict = null)
{
    public static readonly RunOperationVerdict None = new("none");
    public static RunOperationVerdict ForGrade(GradeSnapshot grade) => new("grade", Grade: grade);
    public static RunOperationVerdict ForSecurity(string verdict) => new("security", SecurityVerdict: verdict);
}

/// <summary>One append-only line recording a completed or failed review operation.</summary>
public sealed record RunOperation(
    string RunId,
    string OperationId,
    int Ordinal,
    int Attempt,
    string UnitId,
    string UnitPath,
    string Level,
    string State,
    DateTimeOffset FinishedAt,
    RunOperationVerdict Verdict,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderRunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewedHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InputHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResultSidecarPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null,
    int SchemaVersion = RunOperation.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;
}

/// <summary>One append-only line recording what an operation observed about a finding. Never the lifecycle owner.</summary>
public sealed record RunFinding(
    string RunId,
    string OperationId,
    string FindingId,
    string Fingerprint,
    string RuleId,
    string Severity,
    string Title,
    IReadOnlyList<FindingLocation> Locations,
    string ObservedState,
    DateTimeOffset ObservedAt,
    int SchemaVersion = RunFinding.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;
}

public sealed record RunArchiveCounters(int TotalFiles, int CompletedFiles, int FailedFiles, int SkippedFiles);

public sealed record RunArchiveSpend(
    TokenUsage Usage,
    string PriceStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostSpent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null);

public sealed record RunArchiveQualitySummary(
    int ActiveFindingCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GradeSnapshot? LowestGrade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorstSecurityVerdict = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HighestActiveSeverity = null);

/// <summary>Create-only snapshot of one stopped attempt. A resumed capped run adds another attempt; none is rewritten.</summary>
public sealed record RunAttempt(
    string RunId,
    int AttemptNumber,
    string Outcome,
    string Completeness,
    DateTimeOffset FinishedAt,
    RunArchiveCounters Counters,
    RunArchiveCounters CumulativeCounters,
    RunArchiveSpend Spend,
    RunArchiveSpend CumulativeSpend,
    IReadOnlyList<string> ErrorCodes,
    RunArchiveCap Cap,
    IReadOnlyList<string> LedgerReferences,
    RunArchiveQualitySummary QualitySummary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? StartedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewEstimateDeviation? EstimateDeviation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StopReason = null,
    int SchemaVersion = RunAttempt.CurrentSchemaVersion)
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = SchemaId;
}

public sealed record StoredRunArchive(
    RunRecord Record,
    IReadOnlyList<RunOperation> Operations,
    IReadOnlyList<RunFinding> Findings,
    IReadOnlyList<RunAttempt> Attempts);

/// <summary>Shared serialization for the run-history archive's typed v1 records.</summary>
public static class RunArchiveJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: true);
    public static readonly JsonSerializerOptions LineOptions = CreateOptions(indented: false);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string SerializeLine<T>(T value) => JsonSerializer.Serialize(value, LineOptions);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    /// <summary>Archive timestamps are UTC ISO 8601 with millisecond precision, matching the contract invariant.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || !value.EndsWith('Z') ||
                !DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new JsonException("Archive timestamps must use UTC ISO 8601 with millisecond precision.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            if (value.Offset != TimeSpan.Zero)
                throw new JsonException("Archive timestamps must be a UTC instant.");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// Persists the tracked, Git-committable run-history archive beside the ignored <see cref="ReviewRunStore"/>
/// recovery journal. Every write is either create-only or append-only; nothing here is ever mutated in place.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = RunArchiveJson.Options;
    private static readonly JsonSerializerOptions LineJsonOptions = RunArchiveJson.LineOptions;
    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    /// <summary>Creates the immutable run.json. Throws <see cref="IOException"/> if the run is already archived.</summary>
    public void CreateRun(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(record, JsonOptions));
    }

    public void AppendOperation(RunOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = RequireRunDirectory(operation.RunId);
        AppendLine(Path.Combine(directory, "operations.jsonl"), JsonSerializer.Serialize(operation, LineJsonOptions));
    }

    public void AppendFinding(RunFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = RequireRunDirectory(finding.RunId);
        AppendLine(Path.Combine(directory, "findings.jsonl"), JsonSerializer.Serialize(finding, LineJsonOptions));
    }

    /// <summary>Creates the next attempt snapshot. Throws <see cref="IOException"/> if that attempt number already exists.</summary>
    public void WriteAttempt(RunAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.AttemptNumber < 1)
            throw new ArgumentException("Attempt numbers start at 1.", nameof(attempt));
        var directory = RequireRunDirectory(attempt.RunId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var path = Path.Combine(attemptsDirectory, $"{attempt.AttemptNumber:D4}.json");
        WriteCreateOnly(path, JsonSerializer.Serialize(attempt, JsonOptions));
    }

    /// <summary>Reads one archived run, newest attempt last. Returns null when the run has never been archived.</summary>
    public StoredRunArchive? LoadRun(string runId)
    {
        var directory = FindRunDirectory(runId);
        if (directory is null) return null;
        var record = ReadRequired<RunRecord>(Path.Combine(directory, "run.json"));
        return new StoredRunArchive(record, ReadLines<RunOperation>(Path.Combine(directory, "operations.jsonl")),
            ReadLines<RunFinding>(Path.Combine(directory, "findings.jsonl")), ReadAttempts(directory));
    }

    private IReadOnlyList<RunAttempt> ReadAttempts(string directory)
    {
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        var attempts = new List<RunAttempt>();
        foreach (var path in Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            var attempt = ReadRequired<RunAttempt>(path);
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), attempt.AttemptNumber.ToString("D4"),
                    StringComparison.Ordinal))
                throw new InvalidDataException($"Attempt file name disagrees with its recorded attempt number: {path}");
            attempts.Add(attempt);
        }
        return attempts;
    }

    private static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<T>(line, LineJsonOptions)
                ?? throw new InvalidDataException($"Archive line deserialized to null: {path}");
            items.Add(item);
        }
        return items;
    }

    private static void AppendLine(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(json + "\n");
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
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    private string RequireRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new InvalidOperationException(
            $"Review run '{runId}' has no archived run record. Call {nameof(CreateRun)} first.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (!Directory.Exists(candidate)) continue;
            PathConfinement.RejectReparseTraversal(archiveRoot, candidate);
            return candidate;
        }
        return null;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        var directory = Path.Combine(archiveRoot, month, runId);
        PathConfinement.RejectReparseTraversal(archiveRoot, directory);
        return directory;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId is "." or ".." ||
            !string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Run archive file is empty: {path}");

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
