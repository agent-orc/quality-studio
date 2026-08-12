using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRunReportDocument(
    [property: JsonPropertyName("$schema"), JsonPropertyOrder(0)] string Schema,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] QualityRunIdentity Run,
    [property: JsonPropertyOrder(3)] QualityRunSubject Subject,
    [property: JsonPropertyOrder(4)] QualityRunExecution Execution,
    [property: JsonPropertyOrder(5)] IReadOnlyList<QualityRunObservation> Observations,
    [property: JsonPropertyOrder(6)] QualityRunDelta Delta,
    [property: JsonPropertyOrder(7)] QualityRunSummary Summary);

public sealed record QualityRunIdentity(
    string Id,
    int Revision,
    string RepositoryId,
    string RepositoryName,
    string Kind,
    string ScopeUnitId,
    string Level,
    string Path,
    string State,
    string Completeness,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Model,
    string ThinkingLevel,
    string CliType,
    bool Force);

public sealed record QualityRunSubject(
    string ManifestHash,
    IReadOnlyList<QualityRunSubjectTarget> Targets);

public sealed record QualityRunSubjectTarget(
    string UnitId,
    string Name,
    string Path,
    string SubjectHash);

public sealed record QualityRunExecution(
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    int Cancelled,
    string? AggregateOutcome,
    IReadOnlyList<string> Errors,
    QualityRunUsage Usage,
    QualityRunCap Cap,
    QualityRunEstimateEvidence? Estimate);

public sealed record QualityRunUsage(
    int Operations,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string PriceStatus,
    decimal? InputEstimateDeviationPercent,
    decimal? OutputEstimateDeviationPercent,
    decimal? CostEstimateDeviationPercent);

public sealed record QualityRunCap(
    long? TokenLimit,
    decimal? CostLimit,
    string Outcome,
    string? Reason);

public sealed record QualityRunEstimateEvidence(
    int Files,
    int Operations,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    int HistorySamples,
    string Method);

public sealed record QualityRunObservation(
    string UnitId,
    string Level,
    string Path,
    string Outcome,
    bool ProducedByRun,
    string? SidecarPath,
    string? SidecarSha256,
    DateTimeOffset? CapturedAt,
    string? ReviewedHash,
    string? ProviderRunId,
    QualityRunGrade? Grade,
    string? Summary,
    IReadOnlyList<QualityRunFinding> Findings,
    string? ReviewInputsHash = null);

public sealed record QualityRunGrade(int Score, string Band, string Rationale);

public sealed record QualityRunFinding(
    string Id,
    string RuleId,
    string Aspect,
    string Severity,
    string State,
    string Title,
    string Description,
    string Recommendation,
    string? Evidence,
    string Fingerprint,
    IReadOnlyList<QualityFindingLocation> Locations,
    string Source,
    string? SensorId,
    string? Producer);

public sealed record QualityRunDelta(
    string Status,
    string? PriorRunId,
    string? Reason,
    IReadOnlyList<string> New,
    IReadOnlyList<string> Persisting,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> StateChanged);

public sealed record QualityRunSummary(
    int? Score,
    string? Grade,
    QualityRunFindingCounts Findings,
    string? HighestSeverity,
    string? PartialReason);

public sealed record QualityRunFindingCounts(
    int Total,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByState);

public static class QualityRunReportJson
{
    public const string SchemaId =
        "https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json";

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityRunReportDocument report)
    {
        Validate(report);
        return JsonSerializer.Serialize(report, Options) + Environment.NewLine;
    }

    public static QualityRunReportDocument Deserialize(string json)
    {
        var report = JsonSerializer.Deserialize<QualityRunReportDocument>(json, Options)
            ?? throw new JsonException("Quality run report must be a JSON object.");
        Validate(report);
        return report;
    }

    public static string SubjectManifestHash(IEnumerable<QualityRunSubjectTarget> targets)
    {
        var canonical = new StringBuilder("quality-studio-run-subject-v1\n");
        foreach (var target in targets)
            canonical.Append(target.UnitId).Append('\0').Append(target.Path).Append('\0')
                .Append(target.SubjectHash).Append('\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    public static string ReviewInputsManifestHash(IEnumerable<KeyValuePair<string, string>> unitHashes)
    {
        var canonical = new StringBuilder("quality-studio-run-review-inputs-v1\n");
        foreach (var pair in unitHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            canonical.Append(pair.Key).Append('\0').Append(pair.Value).Append('\n');
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void Validate(QualityRunReportDocument report)
    {
        if (report.SchemaVersion != 1 || !string.Equals(report.Schema, SchemaId, StringComparison.Ordinal))
            throw new JsonException("Unsupported quality run report schema.");
        ArgumentException.ThrowIfNullOrWhiteSpace(report.Run.Id);
        if (report.Run.Revision < 1) throw new JsonException("A quality run report revision must be positive.");
        if (report.Run.Completeness is not ("complete" or "partial"))
            throw new JsonException("A quality run report completeness must be complete or partial.");
    }

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>Atomic repository-owned storage for canonical review-run snapshots.</summary>
public sealed class QualityRunReportStore
{
    public const string RelativeReportsPath = ".quality/reports/runs";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string reportsPath;

    public QualityRunReportStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        reportsPath = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeReportsPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ReportsPath => reportsPath;

    public string PathFor(string runId) => Path.Combine(reportsPath, SafeFileName(runId) + ".json");

    public void Save(QualityRunReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var destination = PathFor(report.Run.Id);
        Directory.CreateDirectory(reportsPath);
        var temporary = Path.Combine(reportsPath, $".{report.Run.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8.GetBytes(QualityRunReportJson.Serialize(report));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public QualityRunReportDocument Load(string runId)
    {
        var path = PathFor(runId);
        if (!File.Exists(path)) throw new FileNotFoundException($"Review run report '{runId}' was not found.", path);
        var report = QualityRunReportJson.Deserialize(File.ReadAllText(path));
        if (!string.Equals(report.Run.Id, runId, StringComparison.Ordinal))
            throw new InvalidDataException($"Review run report '{path}' has a mismatched run id.");
        return report;
    }

    public bool TryLoad(string runId, out QualityRunReportDocument? report)
    {
        try
        {
            report = Load(runId);
            return true;
        }
        catch (FileNotFoundException)
        {
            report = null;
            return false;
        }
    }

    public IReadOnlyList<QualityRunReportDocument> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(reportsPath)) return [];
        var reports = new List<QualityRunReportDocument>();
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(reportsPath, "*.json", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadFailed?.Invoke(reportsPath, exception);
            return [];
        }
        foreach (var path in paths)
        {
            try
            {
                var report = QualityRunReportJson.Deserialize(File.ReadAllText(path));
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), report.Run.Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"Review run report '{path}' has a mismatched run id.");
                reports.Add(report);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   JsonException or InvalidDataException)
            {
                loadFailed?.Invoke(path, exception);
            }
        }
        return reports;
    }

    private static string SafeFileName(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId.Length > 200 || runId.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')))
            throw new ArgumentException("A review run id may contain only letters, digits, dots, underscores, and hyphens.", nameof(runId));
        return runId;
    }
}

public sealed record QualityRunTrendPage(
    IReadOnlyList<QualityRunTrendPoint> Points,
    string? NextCursor);

public sealed record QualityRunTrendPoint(
    string RunId,
    int Revision,
    DateTimeOffset FinishedAt,
    string State,
    string Completeness,
    bool Comparable,
    string? ComparisonReason,
    int? Score,
    string? Grade,
    int ActiveFindings,
    int NewFindings,
    int PersistingFindings,
    int ResolvedFindings,
    int StateChangedFindings,
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost,
    string? Currency);

public static class QualityRunTrendBuilder
{
    public static QualityRunTrendPage Build(
        IEnumerable<QualityRunReportDocument> reports,
        string kind,
        string scopeUnitId,
        string level,
        string? cursor = null,
        int limit = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeUnitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        if (limit is < 1 or > 100) throw new ArgumentException("Trend limit must be between 1 and 100.", nameof(limit));
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, out offset) || offset < 0))
            throw new ArgumentException("Trend cursor must be a non-negative integer.", nameof(cursor));

        var series = reports
            .Where(report => string.Equals(report.Run.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(report.Run.ScopeUnitId, scopeUnitId, StringComparison.Ordinal) &&
                             string.Equals(report.Run.Level, level, StringComparison.OrdinalIgnoreCase) &&
                             report.Run.FinishedAt.HasValue)
            .GroupBy(report => report.Run.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(report => report.Run.Revision).First())
            .OrderByDescending(report => report.Run.FinishedAt)
            .ThenByDescending(report => report.Run.Id, StringComparer.Ordinal)
            .ToArray();
        var selected = series.Skip(offset).Take(limit).Select(report =>
        {
            var comparable = report.Run.State == "done" && report.Run.Completeness == "complete" &&
                             report.Summary.Score.HasValue;
            return new QualityRunTrendPoint(
                report.Run.Id,
                report.Run.Revision,
                report.Run.FinishedAt!.Value,
                report.Run.State,
                report.Run.Completeness,
                comparable,
                comparable ? null : report.Summary.PartialReason ?? $"Run state is {report.Run.State}.",
                comparable ? report.Summary.Score : null,
                comparable ? report.Summary.Grade : null,
                report.Observations.SelectMany(observation => observation.Findings)
                    .Where(finding => finding.State is "open" or "accepted")
                    .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).Count(),
                report.Delta.New.Count,
                report.Delta.Persisting.Count,
                report.Delta.Resolved.Count,
                report.Delta.StateChanged.Count,
                report.Execution.Reviewed,
                report.Execution.ReusedFresh,
                report.Execution.Failed,
                report.Execution.Skipped,
                report.Execution.Usage.InputTokens,
                report.Execution.Usage.OutputTokens,
                report.Execution.Usage.Cost,
                report.Execution.Usage.Currency);
        }).ToArray();
        var next = offset + selected.Length < series.Length
            ? (offset + selected.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new QualityRunTrendPage(selected, next);
    }
}
