using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Deterministic run-archive operation ids (see the operationId invariant in
/// docs/operations/run-persistence/index.html#contract). Stable for the same run, attempt and target
/// path so a crash-recovery requeue within an attempt reuses the same id; a new attempt after a
/// capped resume produces a fresh one for the same target.
/// </summary>
public static class ReviewRunOperationIdentity
{
    public static string Compute(string runId, int attempt, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempt numbers start at 1.");
        return "op-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}\0{attempt}\0{targetPath}")));
    }
}

/// <summary>
/// Immutable run identity, plan and configuration. Create-only: written once when a review run is
/// enqueued and never rewritten by later attempts. See docs/operations/run-persistence/index.html.
/// </summary>
public sealed record RunRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-record.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required string RepositoryId { get; init; }

    [JsonPropertyOrder(4)]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyOrder(5)]
    public required RunRecordSubject Subject { get; init; }

    [JsonPropertyOrder(6)]
    public required string Kind { get; init; }

    [JsonPropertyOrder(7)]
    public required IReadOnlyList<RunRecordTarget> Targets { get; init; }

    [JsonPropertyOrder(8)]
    public required RunRecordConfiguration Configuration { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunRecordEstimate? Estimate { get; init; }

    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TokenCap { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostCap { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunRecordSourceRevision? SourceRevision { get; init; }
}

public sealed record RunRecordSubject(string NodeId, string NodeName, string NodePath, string Level);

public sealed record RunRecordTarget(string Path, string SubjectHash);

public sealed record RunRecordConfiguration(
    string CliType,
    bool Force,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AggregateControls = null);

public sealed record RunRecordEstimate(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    string PriceStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Cost = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null);

public sealed record RunRecordSourceRevision(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommitSha = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Dirty = null);

/// <summary>
/// One operation's typed quality result. Grade and security producers keep their own vocabulary
/// rather than being coerced into a universal pass/fail (docs/operations/run-persistence/index.html#verdicts).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GradeOperationVerdict), "grade")]
[JsonDerivedType(typeof(SecurityOperationVerdict), "security")]
public abstract record RunOperationVerdict;

public sealed record GradeOperationVerdict(int Score, string Band) : RunOperationVerdict;

public sealed record SecurityOperationVerdict(SecurityVerdict Verdict) : RunOperationVerdict;

/// <summary>
/// Append-only record of one completed or failed review operation. Appended only after the result
/// sidecar is atomically written, or after a terminal operation error is known.
/// </summary>
public sealed record RunOperationRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-operation.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(3)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(4)]
    public required int Attempt { get; init; }

    [JsonPropertyOrder(5)]
    public required int Ordinal { get; init; }

    [JsonPropertyOrder(6)]
    public required string UnitPath { get; init; }

    [JsonPropertyOrder(7)]
    public required string Level { get; init; }

    [JsonPropertyOrder(8)]
    public required string State { get; init; }

    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderRunId { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewedHash { get; init; }

    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReviewInputsHash { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultSidecarPath { get; init; }

    [JsonPropertyOrder(15), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunOperationVerdict? Verdict { get; init; }
}

/// <summary>
/// Append-only observation of one finding at the time an operation produced it. Never becomes the
/// current lifecycle owner; that remains .quality/findings/state.json.
/// </summary>
public sealed record RunFindingRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-finding.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string OperationId { get; init; }

    [JsonPropertyOrder(3)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(4)]
    public required string Fingerprint { get; init; }

    [JsonPropertyOrder(5)]
    public required string FindingId { get; init; }

    [JsonPropertyOrder(6)]
    public required string RuleId { get; init; }

    [JsonPropertyOrder(7)]
    public required FindingSeverity Severity { get; init; }

    [JsonPropertyOrder(8)]
    public required string Title { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<FindingLocation> Locations { get; init; }

    [JsonPropertyOrder(10)]
    public required string State { get; init; }

    [JsonPropertyOrder(11)]
    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record RunAttemptCompleteness(int TotalFiles, int CompletedFiles, int FailedFiles, int SkippedFiles);

public sealed record RunAttemptSpend(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    string PriceStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Cost = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Currency = null);

public sealed record RunAttemptQualitySummary(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GradeSnapshot? LowestGrade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SecurityVerdict? WorstSecurityVerdict = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ActiveFindingCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FindingSeverity? HighestActiveSeverity = null);

/// <summary>
/// Create-only snapshot of one stopped attempt. A capped run that resumes gets a new, higher-numbered
/// attempt record; earlier attempts are never rewritten.
/// </summary>
public sealed record RunAttemptRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/run-attempt.v1.schema.json";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required string RunId { get; init; }

    [JsonPropertyOrder(3)]
    public required int AttemptNumber { get; init; }

    [JsonPropertyOrder(4)]
    public required string Outcome { get; init; }

    [JsonPropertyOrder(5)]
    public required RunAttemptCompleteness Completeness { get; init; }

    [JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyOrder(7)]
    public required DateTimeOffset FinishedAt { get; init; }

    [JsonPropertyOrder(8)]
    public required RunAttemptSpend Spend { get; init; }

    [JsonPropertyOrder(9)]
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];

    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TokenCap { get; init; }

    [JsonPropertyOrder(11), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostCap { get; init; }

    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewEstimateDeviation? EstimateDeviation { get; init; }

    [JsonPropertyOrder(13)]
    public IReadOnlyList<string> LedgerReferences { get; init; } = [];

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunAttemptQualitySummary? QualitySummary { get; init; }
}

/// <summary>Shared JSON contract for the four run-archive v1 schemas.</summary>
public static class ReviewRunArchiveJson
{
    public static JsonSerializerOptions DocumentOptions { get; } = CreateOptions(indented: true);
    public static JsonSerializerOptions LineOptions { get; } = CreateOptions(indented: false);

    public static string SerializeRun(RunRecord run) => Serialize(run, run.Schema, run.SchemaVersion, RunRecord.SchemaId, RunRecord.CurrentSchemaVersion, DocumentOptions);
    public static string SerializeOperation(RunOperationRecord operation) => Serialize(operation, operation.Schema, operation.SchemaVersion, RunOperationRecord.SchemaId, RunOperationRecord.CurrentSchemaVersion, LineOptions);
    public static string SerializeFinding(RunFindingRecord finding) => Serialize(finding, finding.Schema, finding.SchemaVersion, RunFindingRecord.SchemaId, RunFindingRecord.CurrentSchemaVersion, LineOptions);
    public static string SerializeAttempt(RunAttemptRecord attempt) => Serialize(attempt, attempt.Schema, attempt.SchemaVersion, RunAttemptRecord.SchemaId, RunAttemptRecord.CurrentSchemaVersion, DocumentOptions);

    public static RunRecord DeserializeRun(string json) =>
        Validate(JsonSerializer.Deserialize<RunRecord>(json, DocumentOptions) ?? throw new JsonException("Run record must be a JSON object."),
            run => run.Schema, run => run.SchemaVersion, RunRecord.SchemaId, RunRecord.CurrentSchemaVersion, "run record");

    public static RunOperationRecord DeserializeOperation(string json) =>
        Validate(JsonSerializer.Deserialize<RunOperationRecord>(json, LineOptions) ?? throw new JsonException("Run operation must be a JSON object."),
            operation => operation.Schema, operation => operation.SchemaVersion, RunOperationRecord.SchemaId, RunOperationRecord.CurrentSchemaVersion, "run operation");

    public static RunFindingRecord DeserializeFinding(string json) =>
        Validate(JsonSerializer.Deserialize<RunFindingRecord>(json, LineOptions) ?? throw new JsonException("Run finding must be a JSON object."),
            finding => finding.Schema, finding => finding.SchemaVersion, RunFindingRecord.SchemaId, RunFindingRecord.CurrentSchemaVersion, "run finding");

    public static RunAttemptRecord DeserializeAttempt(string json) =>
        Validate(JsonSerializer.Deserialize<RunAttemptRecord>(json, DocumentOptions) ?? throw new JsonException("Run attempt must be a JSON object."),
            attempt => attempt.Schema, attempt => attempt.SchemaVersion, RunAttemptRecord.SchemaId, RunAttemptRecord.CurrentSchemaVersion, "run attempt");

    private static string Serialize<T>(T value, string schema, int schemaVersion, string expectedSchema, int expectedVersion, JsonSerializerOptions options)
    {
        if (!string.Equals(schema, expectedSchema, StringComparison.Ordinal) || schemaVersion != expectedVersion)
            throw new JsonException($"Unsupported schema '{schema}' version {schemaVersion}; expected '{expectedSchema}' version {expectedVersion}.");
        return JsonSerializer.Serialize(value, options);
    }

    private static T Validate<T>(T value, Func<T, string> schema, Func<T, int> version, string expectedSchema, int expectedVersion, string label)
    {
        if (!string.Equals(schema(value), expectedSchema, StringComparison.Ordinal) || version(value) != expectedVersion)
            throw new JsonException($"Unsupported {label} schema '{schema(value)}' version {version(value)}; expected '{expectedSchema}' version {expectedVersion}.");
        return value;
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}
