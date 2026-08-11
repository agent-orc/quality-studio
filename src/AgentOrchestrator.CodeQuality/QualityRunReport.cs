using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRunReportDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    QualityRunIdentity Run,
    QualityRunSubject Subject,
    QualityRunExecution Execution,
    IReadOnlyList<QualityRunObservation> Observations,
    QualityRunDelta Delta,
    QualityRunSummary Summary);

public sealed record QualityRunIdentity(
    string Id,
    int Revision,
    string RepositoryId,
    string RepositoryName,
    string Kind,
    QualityRunScope Scope,
    string State,
    string Completeness,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Model,
    string CliType,
    bool Force,
    string? ThinkingLevel = null);

public sealed record QualityRunScope(string UnitId, string Level, string Path, string DisplayName);

public sealed record QualityRunSubject(string ManifestHash, IReadOnlyList<QualityRunTarget> Targets);

public sealed record QualityRunTarget(string UnitId, string Path, string SubjectHash);

public sealed record QualityRunExecution(
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    int Cancelled,
    string? AggregateOutcome,
    IReadOnlyList<string> Errors,
    int UsageOperations,
    TokenUsage Usage,
    long? TokenCap,
    decimal? CostCap,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    string? StopReason);

public sealed record QualityRunObservation(
    string Path,
    string Level,
    string Outcome,
    bool ProducedByRun,
    bool Aggregate,
    string? SidecarPath,
    string? SidecarSha256,
    string? ReviewedHash,
    string? ProviderRunId,
    DateTimeOffset? ReviewedAt,
    QualityRunGrade? Grade,
    string? Summary,
    IReadOnlyList<QualityRunFinding> Findings,
    IReadOnlyList<JsonElement> DeterministicEvidence);

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
    string Fingerprint,
    IReadOnlyList<QualityFindingLocation> Locations,
    string Source = "agent",
    string? SensorId = null,
    string? Producer = null,
    string? Evidence = null,
    string? StateAuthor = null,
    string? StateReason = null);

public sealed record QualityRunDelta(
    string Status,
    string? Reason,
    string? PreviousRunId,
    IReadOnlyList<string> New,
    IReadOnlyList<string> Persisting,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> StateChanged);

public sealed record QualityRunSummary(
    int? Score,
    string? Grade,
    FindingCounts Findings,
    string? HighestSeverity,
    string? PartialReason);

public sealed record QualityRunReportBuildInput(
    string RunId,
    int Revision,
    string RepositoryId,
    string RepositoryName,
    string Kind,
    QualityRunScope Scope,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Model,
    string CliType,
    bool Force,
    IReadOnlyList<QualityRunTarget> Targets,
    IReadOnlyList<QualityRunOperationInput> Operations,
    QualityRunOperationInput? AggregateOperation,
    IReadOnlyList<string> Errors,
    int UsageOperations,
    TokenUsage Usage,
    long? TokenCap,
    decimal? CostCap,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    string? StopReason,
    string? ThinkingLevel = null);

public sealed record QualityRunOperationInput(
    string Path,
    string Level,
    string Outcome,
    bool Aggregate,
    ReviewObservationSnapshot? Snapshot);

public static class QualityRunReportJson
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json";
    public const int CurrentSchemaVersion = 1;
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityRunReportDocument report)
    {
        Validate(report);
        return JsonSerializer.Serialize(report, Options) + Environment.NewLine;
    }

    public static QualityRunReportDocument Deserialize(string json)
    {
        var report = JsonSerializer.Deserialize<QualityRunReportDocument>(json, Options)
            ?? throw new JsonException("A quality run report must be a JSON object.");
        Validate(report);
        return report;
    }

    private static void Validate(QualityRunReportDocument report)
    {
        if (!string.Equals(report.Schema, SchemaId, StringComparison.Ordinal) ||
            report.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException("Unsupported quality run report schema version.");
        if (string.IsNullOrWhiteSpace(report.Run.Id)) throw new JsonException("A quality run report requires a run id.");
        if (report.Run.Revision < 1) throw new JsonException("A quality run report revision must be positive.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Default,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        return options;
    }
}

/// <summary>Atomic repository-owned storage for canonical review-run reports.</summary>
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

    public string Write(QualityRunReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var destination = ReportPath(report.Run.Id);
        Directory.CreateDirectory(reportsPath);
        var temporary = Path.Combine(reportsPath, $"{report.Run.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8.GetBytes(QualityRunReportJson.Serialize(report));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public QualityRunReportDocument Load(string runId) =>
        QualityRunReportJson.Deserialize(File.ReadAllText(ReportPath(runId)));

    public bool TryLoad(string runId, out QualityRunReportDocument? report)
    {
        try
        {
            var path = ReportPath(runId);
            report = File.Exists(path) ? QualityRunReportJson.Deserialize(File.ReadAllText(path)) : null;
            return report is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            report = null;
            return false;
        }
    }

    public IReadOnlyList<QualityRunReportDocument> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(reportsPath)) return [];
        var reports = new List<QualityRunReportDocument>();
        foreach (var path in Directory.EnumerateFiles(reportsPath, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                var report = QualityRunReportJson.Deserialize(File.ReadAllText(path));
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), report.Run.Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"Run report filename and run id disagree at '{path}'.");
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

    private string ReportPath(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
        return Path.Combine(reportsPath, runId + ".json");
    }
}

public static class QualityRunReportBuilder
{
    private static readonly string[] Severities = ["critical", "high", "medium", "low", "info"];
    private static readonly string[] States = ["open", "accepted", "waived", "false-positive", "resolved"];

    public static QualityRunReportDocument Build(
        string repositoryRoot,
        QualityRunReportBuildInput input,
        IReadOnlyList<QualityRunReportDocument>? existingReports = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(input);
        var root = Path.GetFullPath(repositoryRoot);
        var operations = input.Operations.OrderBy(operation => operation.Path, StringComparer.Ordinal).ToArray();
        var aggregate = input.AggregateOperation;
        var complete = input.State == "done" &&
                       operations.All(operation => operation.Outcome is "done" or "skipped-fresh") &&
                       (aggregate is null || aggregate.Outcome is "done" or "skipped-fresh");
        var observations = operations.Select(operation => Capture(root, operation))
            .Concat(aggregate is null ? [] : [Capture(root, aggregate)])
            .ToArray();
        var findings = observations.SelectMany(observation => observation.Findings)
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.OrderBy(finding => StateOrder(finding.State)).First())
            .ToArray();
        var scopeObservation = aggregate is null
            ? observations.SingleOrDefault(observation => !observation.Aggregate)
            : observations.SingleOrDefault(observation => observation.Aggregate);
        var score = complete ? scopeObservation?.Grade?.Score : null;
        var grade = score.HasValue ? QualityReportBuilder.Grade(score.Value) : null;
        var stopReason = input.StopReason is null ? null : Sanitize(input.StopReason, root);
        var partialReason = complete && score.HasValue
            ? null
            : stopReason ?? (input.State == "done"
                ? "The completed scope did not contain a score-bearing observation."
                : $"Run state is {input.State}; partial runs do not publish a comparable score.");
        var subject = new QualityRunSubject(ManifestHash(input.Targets), input.Targets);
        var run = new QualityRunIdentity(
            input.RunId, input.Revision, input.RepositoryId, input.RepositoryName, input.Kind, input.Scope,
            input.State, complete ? "complete" : "partial", input.CreatedAt, input.StartedAt, input.FinishedAt,
            input.Model, input.CliType, input.Force, input.ThinkingLevel);
        var delta = Delta(run, findings, existingReports ?? []);
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            QualityRunReportJson.CurrentSchemaVersion,
            run,
            subject,
            new QualityRunExecution(
                operations.Count(operation => operation.Outcome == "done"),
                operations.Count(operation => operation.Outcome == "skipped-fresh"),
                operations.Count(operation => operation.Outcome == "failed"),
                operations.Count(operation => operation.Outcome == "skipped"),
                operations.Count(operation => operation.Outcome == "cancelled"),
                aggregate?.Outcome,
                input.Errors.Select(error => Sanitize(error, root)).Where(error => error.Length > 0).ToArray(),
                input.UsageOperations, input.Usage, input.TokenCap, input.CostCap, input.CostSpent,
                input.Currency, input.PriceStatus, stopReason),
            observations,
            delta,
            new QualityRunSummary(score, grade, CountFindings(findings), HighestSeverity(findings), partialReason));
    }

    private static QualityRunObservation Capture(string root, QualityRunOperationInput operation)
    {
        if (operation.Snapshot is null)
            return new QualityRunObservation(operation.Path, operation.Level, operation.Outcome,
                false, operation.Aggregate, null, null, null, null, null, null, null, [], []);

        var sidecarPath = Path.GetFullPath(operation.Snapshot.MetaPath);
        if (!IsContained(root, sidecarPath))
            throw new InvalidDataException("A run observation sidecar must be inside the repository root.");
        var relativeSidecar = Normalize(Path.GetRelativePath(root, sidecarPath));
        var metadata = JsonNode.Parse(operation.Snapshot.ProjectedJson)?.AsObject()
            ?? throw new JsonException($"Run observation '{operation.Path}' must be an object.");
        var level = metadata["unit"]?["level"]?.GetValue<string>() ?? operation.Level;
        var gradeNode = metadata["grade"]?.AsObject();
        QualityRunGrade? grade = null;
        if (gradeNode?["score"]?.GetValue<int>() is { } score)
        {
            grade = new QualityRunGrade(Math.Clamp(score, 0, 100),
                gradeNode["band"]?.GetValue<string>() ?? QualityReportBuilder.Grade(score),
                gradeNode["rationale"]?.GetValue<string>() ?? string.Empty);
        }
        var findings = ParseFindings(metadata).ToArray();
        var evidence = (metadata["deterministicEvidence"]?.AsArray() ?? [])
            .Select(item => JsonSerializer.SerializeToElement(item, QualityRunReportJson.Options)).ToArray();
        return new QualityRunObservation(
            Normalize(operation.Path), level, operation.Outcome, operation.Outcome == "done", operation.Aggregate,
            relativeSidecar,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operation.Snapshot.SidecarJson))),
            metadata["reviewedHash"]?["value"]?.GetValue<string>(),
            metadata["reviewer"]?["runId"]?.GetValue<string>(),
            ParseDate(metadata["reviewedAt"]?.GetValue<string>()),
            grade,
            metadata["summary"]?.GetValue<string>(),
            findings,
            evidence);
    }

    private static IEnumerable<QualityRunFinding> ParseFindings(JsonObject metadata)
    {
        foreach (var finding in metadata["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (ParseFinding(finding, "agent", null, null) is { } parsed) yield return parsed;
        }
        foreach (var sensor in metadata["deterministicEvidence"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var sensorId = sensor["provenance"]?["sensorId"]?.GetValue<string>();
            foreach (var finding in sensor["findings"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var producer = finding["source"]?["producer"]?.GetValue<string>();
                if (ParseFinding(finding, "deterministic", sensorId, producer) is { } parsed) yield return parsed;
            }
        }
    }

    private static QualityRunFinding? ParseFinding(JsonObject finding, string source, string? sensorId, string? producer)
    {
        var id = finding["id"]?.GetValue<string>();
        if (id is null) return null;
        var ruleId = finding["ruleId"]?.GetValue<string>() ?? id;
        var locations = (finding["locations"]?.AsArray().OfType<JsonObject>() ?? []).Select(location =>
        {
            var range = location["range"]?.AsObject();
            return new QualityFindingLocation(
                Normalize(location["path"]?.GetValue<string>() ?? "."),
                IntAt(range, "start", "line"), IntAt(range, "start", "column"),
                IntAt(range, "end", "line"), IntAt(range, "end", "column"));
        }).ToArray();
        return new QualityRunFinding(
            id, ruleId, finding["aspect"]?.GetValue<string>() ?? "general",
            finding["severity"]?.GetValue<string>()?.ToLowerInvariant() ?? "info",
            finding["state"]?.GetValue<string>()?.ToLowerInvariant() ?? "open",
            finding["title"]?.GetValue<string>() ?? id,
            finding["description"]?.GetValue<string>() ?? string.Empty,
            finding["recommendation"]?.GetValue<string>() ?? string.Empty,
            finding["fingerprint"]?.GetValue<string>() ?? LegacyFingerprint(ruleId, finding, locations),
            locations, source, sensorId, producer,
            finding["evidence"]?.GetValue<string>(), finding["stateAuthor"]?.GetValue<string>(),
            finding["stateReason"]?.GetValue<string>());
    }

    private static QualityRunDelta Delta(
        QualityRunIdentity run,
        IReadOnlyList<QualityRunFinding> currentFindings,
        IReadOnlyList<QualityRunReportDocument> existing)
    {
        if (run.Completeness != "complete" || run.State != "done")
            return new QualityRunDelta("unavailable", "The current run is partial.", null, [], [], [], []);
        var prior = existing
            .Where(candidate => candidate.Run.Id != run.Id && candidate.Run.State == "done" &&
                                candidate.Run.Completeness == "complete" && SameSeries(candidate.Run, run))
            .GroupBy(candidate => candidate.Run.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Run.Revision).First())
            .Where(candidate => candidate.Run.FinishedAt <= run.FinishedAt)
            .OrderByDescending(candidate => candidate.Run.FinishedAt)
            .FirstOrDefault();
        if (prior is null)
            return new QualityRunDelta("unavailable", "No prior comparable run snapshot exists.", null, [], [], [], []);
        var current = ActiveFindings(currentFindings);
        var previous = ActiveFindings(prior.Observations.SelectMany(observation => observation.Findings));
        var currentKeys = current.Keys.ToHashSet(StringComparer.Ordinal);
        var previousKeys = previous.Keys.ToHashSet(StringComparer.Ordinal);
        return new QualityRunDelta(
            "available", null, prior.Run.Id,
            currentKeys.Except(previousKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            currentKeys.Intersect(previousKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            previousKeys.Except(currentKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            currentKeys.Intersect(previousKeys, StringComparer.Ordinal)
                .Where(key => !string.Equals(current[key].State, previous[key].State, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, QualityRunFinding> ActiveFindings(IEnumerable<QualityRunFinding> findings) =>
        findings.Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    private static bool SameSeries(QualityRunIdentity left, QualityRunIdentity right) =>
        string.Equals(left.RepositoryId, right.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Scope.UnitId, right.Scope.UnitId, StringComparison.Ordinal) &&
        string.Equals(left.Scope.Level, right.Scope.Level, StringComparison.Ordinal);

    private static FindingCounts CountFindings(IReadOnlyList<QualityRunFinding> findings)
    {
        var bySeverity = Severities.ToDictionary(severity => severity,
            severity => findings.Count(finding => finding.Severity == severity), StringComparer.Ordinal);
        var byState = States.ToDictionary(state => state,
            state => findings.Count(finding => finding.State == state), StringComparer.Ordinal);
        return new FindingCounts(findings.Count, bySeverity, byState);
    }

    private static string? HighestSeverity(IEnumerable<QualityRunFinding> findings) =>
        Severities.FirstOrDefault(severity => findings.Any(finding => finding.State != "resolved" && finding.Severity == severity));

    private static string ManifestHash(IReadOnlyList<QualityRunTarget> targets)
    {
        var canonical = string.Join('\n', targets.Select(target =>
            string.Join('\0', target.UnitId, Normalize(target.Path), target.SubjectHash)));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string LegacyFingerprint(string ruleId, JsonObject finding, IReadOnlyList<QualityFindingLocation> locations)
    {
        var primary = locations.FirstOrDefault();
        var canonical = string.Join('\0', "quality-studio-run-report-legacy-finding-v1", ruleId,
            primary?.Path ?? ".", primary?.StartLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            finding["title"]?.GetValue<string>() ?? string.Empty);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static int? IntAt(JsonObject? parent, string objectName, string propertyName) =>
        parent?[objectName]?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Sanitize(string value, string root)
    {
        var normalizedRoot = Normalize(root).TrimEnd('/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var sanitized = Normalize(value).Replace(normalizedRoot, ".", comparison)
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized[..Math.Min(2000, sanitized.Length)];
    }

    private static int StateOrder(string state) => state switch
    {
        "open" => 0,
        "accepted" => 1,
        "waived" => 2,
        "false-positive" => 3,
        "resolved" => 4,
        _ => 5,
    };

    private static string Normalize(string value) => value.Replace('\\', '/');

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}

public sealed record QualityRunTrendPage(
    string RepositoryId,
    string Kind,
    QualityRunScope Scope,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<QualityRunTrendPoint> Points);

public sealed record QualityRunTrendPoint(
    string RunId,
    int Revision,
    DateTimeOffset? FinishedAt,
    string State,
    string Completeness,
    bool Comparable,
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
    long DurationMs,
    decimal? Cost,
    string? Currency,
    string? PartialReason);

public static class QualityRunTrendBuilder
{
    public static QualityRunTrendPage Build(
        IReadOnlyList<QualityRunReportDocument> reports,
        string repositoryId,
        string kind,
        string scopeUnitId,
        string level,
        int page = 1,
        int pageSize = 30)
    {
        if (page < 1) throw new ArgumentException("Trend page must be at least 1.", nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentException("Trend page size must be between 1 and 100.", nameof(pageSize));
        var selected = reports.Where(report =>
                string.Equals(report.Run.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(report.Run.Kind, kind, StringComparison.Ordinal) &&
                string.Equals(report.Run.Scope.UnitId, scopeUnitId, StringComparison.Ordinal) &&
                string.Equals(report.Run.Scope.Level, level, StringComparison.Ordinal))
            .GroupBy(report => report.Run.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(report => report.Run.Revision).First())
            .OrderByDescending(report => report.Run.FinishedAt)
            .ToArray();
        var scope = selected.FirstOrDefault()?.Run.Scope ?? new QualityRunScope(scopeUnitId, level, ".", scopeUnitId);
        var points = selected.Skip((page - 1) * pageSize).Take(pageSize).Select(report =>
        {
            var active = report.Observations.SelectMany(observation => observation.Findings)
                .Where(finding => finding.State != "resolved")
                .Select(finding => finding.Fingerprint).Distinct(StringComparer.Ordinal).Count();
            var delta = report.Delta;
            return new QualityRunTrendPoint(
                report.Run.Id, report.Run.Revision, report.Run.FinishedAt, report.Run.State,
                report.Run.Completeness, report.Run.Completeness == "complete" && report.Summary.Score.HasValue,
                report.Summary.Score, report.Summary.Grade, active,
                delta.Status == "available" ? delta.New.Count : 0,
                delta.Status == "available" ? delta.Persisting.Count : 0,
                delta.Status == "available" ? delta.Resolved.Count : 0,
                delta.Status == "available" ? delta.StateChanged.Count : 0,
                report.Execution.Reviewed, report.Execution.ReusedFresh, report.Execution.Failed,
                report.Execution.Skipped, report.Execution.Usage.InputTokens, report.Execution.Usage.OutputTokens,
                report.Execution.Usage.DurationMs, report.Execution.CostSpent, report.Execution.Currency,
                report.Summary.PartialReason);
        }).ToArray();
        return new QualityRunTrendPage(repositoryId, kind, scope, page, pageSize, selected.Length, points);
    }
}

public static class QualityRunReportRenderer
{
    public const int MarkdownFindingLimit = 20;
    public const string SarifSchema = QualityReportRenderer.SarifSchema;

    public static string Render(QualityRunReportDocument report, QualityReportFormat format) => format switch
    {
        QualityReportFormat.Json => QualityRunReportJson.Serialize(report),
        QualityReportFormat.Markdown => Markdown(report),
        QualityReportFormat.Html => Html(report),
        QualityReportFormat.Sarif => Sarif(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string Markdown(QualityRunReportDocument report)
    {
        var text = new StringBuilder();
        var run = report.Run;
        var execution = report.Execution;
        var active = Active(report).OrderBy(finding => SeverityOrder(finding.Severity))
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal).ToArray();
        text.AppendLine("# Quality Studio review run");
        text.AppendLine();
        text.AppendLine($"**{run.State.ToUpperInvariant()} · {EscapeMarkdown(run.Kind)} · {EscapeMarkdown(run.Scope.Level)} `{EscapeMarkdown(run.Scope.Path)}` · {run.Completeness}**");
        text.AppendLine();
        text.AppendLine($"Run `{EscapeMarkdown(run.Id)}` revision {run.Revision} · {report.Subject.Targets.Count} targets · {execution.Reviewed} reviewed · {execution.ReusedFresh} reused · {execution.Failed} failed");
        text.AppendLine();
        text.AppendLine(report.Summary.Score.HasValue
            ? $"Score {report.Summary.Score}/100 ({report.Summary.Grade}) · {active.Length} active findings · {JoinSeverity(report.Summary.Findings.BySeverity)}"
            : $"Score unavailable ({EscapeMarkdown(report.Summary.PartialReason ?? "partial run")}) · {active.Length} active findings");
        text.AppendLine();
        text.AppendLine(report.Delta.Status == "available"
            ? $"Delta from `{EscapeMarkdown(report.Delta.PreviousRunId!)}`: {report.Delta.New.Count} new · {report.Delta.Persisting.Count} persisting · {report.Delta.Resolved.Count} resolved · {report.Delta.StateChanged.Count} state changed"
            : $"Delta: unavailable ({EscapeMarkdown(report.Delta.Reason ?? "no comparable baseline")})");
        text.AppendLine();
        text.AppendLine("## Active findings");
        text.AppendLine();
        foreach (var (finding, index) in active.Take(MarkdownFindingLimit).Select((finding, index) => (finding, index)))
        {
            text.AppendLine($"{index + 1}. [{EscapeMarkdown(finding.Severity)}/{EscapeMarkdown(finding.State)}] {EscapeMarkdown(finding.Title)}");
            var location = finding.Locations.FirstOrDefault();
            text.AppendLine($"   {(location is null ? "no physical location" : EscapeMarkdown(location.Path) + (location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty))} · {EscapeMarkdown(finding.RuleId)}");
        }
        if (active.Length > MarkdownFindingLimit)
        {
            text.AppendLine();
            text.AppendLine($"{active.Length - MarkdownFindingLimit} additional active finding(s) are available in JSON and SARIF.");
        }
        text.AppendLine();
        text.AppendLine($"Gate inputs: score {(report.Summary.Score?.ToString(CultureInfo.InvariantCulture) ?? "unavailable")} · highest severity {report.Summary.HighestSeverity ?? "none"}.");
        return text.ToString();
    }

    private static string Html(QualityRunReportDocument report)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var run = report.Run;
        var active = Active(report).OrderBy(finding => SeverityOrder(finding.Severity)).ToArray();
        var text = new StringBuilder();
        text.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        text.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\">");
        text.Append("<title>Quality Studio review run ").Append(H(run.Id)).Append("</title><style>");
        text.Append(":root{color-scheme:light dark;--bg:#fbfbfa;--surface:#f1f1ee;--ink:#171716;--muted:#65635e;--line:#d8d5ce;--ok:#257942;--warn:#956800;--bad:#b53636}@media(prefers-color-scheme:dark){:root{--bg:#1b1b1a;--surface:#272725;--ink:#f5f5f2;--muted:#b8b5ad;--line:#44423e;--ok:#68c486;--warn:#ddb95b;--bad:#ef8080}}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,sans-serif}main{max-width:72rem;margin:auto;padding:2rem}header{padding-bottom:1.5rem;border-bottom:1px solid var(--line)}h1{margin:.25rem 0;font-size:2rem}h2{margin-top:2.5rem}.muted{color:var(--muted)}.state{display:inline-block;padding:.2rem .55rem;border-radius:.3rem;background:var(--surface);font-weight:700;text-transform:uppercase}.partial{color:var(--warn)}dl{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:1px;background:var(--line);border:1px solid var(--line)}dl div{padding:1rem;background:var(--surface)}dt{color:var(--muted);font-size:.75rem;text-transform:uppercase}dd{margin:.25rem 0 0;font-weight:700}.finding{padding:1rem 0;border-top:1px solid var(--line)}.finding h3{margin:.35rem 0;font-size:1rem}.finding p{max-width:78ch}.severity{color:var(--warn);font-size:.75rem;font-weight:700;text-transform:uppercase}code{overflow-wrap:anywhere}@media(max-width:44rem){main{padding:1rem}dl{grid-template-columns:1fr 1fr}}@media print{body{background:#fff;color:#000}main{max-width:none;padding:0}.finding{break-inside:avoid}}@media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important}}</style></head><body><main>");
        text.Append("<header><p class=\"muted\">Quality Studio · canonical run report</p><h1>").Append(H(run.Scope.DisplayName)).Append("</h1><p><span class=\"state ").Append(run.Completeness == "partial" ? "partial" : string.Empty).Append("\">").Append(H(run.State)).Append(" · ").Append(H(run.Completeness)).Append("</span></p><p class=\"muted\">Run <code>").Append(H(run.Id)).Append("</code> revision ").Append(run.Revision).Append(" · ").Append(H(run.Kind)).Append(" · ").Append(H(run.Scope.Path)).Append("</p></header>");
        text.Append("<h2>Outcome</h2><dl><div><dt>Score</dt><dd>").Append(report.Summary.Score?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable").Append(report.Summary.Grade is null ? string.Empty : " / " + H(report.Summary.Grade)).Append("</dd></div><div><dt>Reviewed</dt><dd>").Append(report.Execution.Reviewed).Append("</dd></div><div><dt>Reused fresh</dt><dd>").Append(report.Execution.ReusedFresh).Append("</dd></div><div><dt>Failed</dt><dd>").Append(report.Execution.Failed).Append("</dd></div></dl>");
        text.Append("<h2>Finding delta</h2><p>");
        if (report.Delta.Status == "available") text.Append(report.Delta.New.Count).Append(" new · ").Append(report.Delta.Persisting.Count).Append(" persisting · ").Append(report.Delta.Resolved.Count).Append(" resolved · ").Append(report.Delta.StateChanged.Count).Append(" state changed");
        else text.Append("Unavailable: ").Append(H(report.Delta.Reason));
        text.Append("</p><h2>Active findings</h2>");
        foreach (var finding in active)
        {
            var location = finding.Locations.FirstOrDefault();
            text.Append("<article class=\"finding\"><span class=\"severity\">").Append(H(finding.Severity)).Append(" · ").Append(H(finding.State)).Append("</span><h3>").Append(H(finding.Title)).Append("</h3><p>").Append(H(finding.Description)).Append("</p><p><b>Recommendation:</b> ").Append(H(finding.Recommendation)).Append("</p><p class=\"muted\"><code>").Append(H(finding.RuleId)).Append("</code>");
            if (location is not null) text.Append(" · ").Append(H(location.Path)).Append(location.StartLine.HasValue ? ":" + location.StartLine : string.Empty);
            text.Append("</p>");
            if (!string.IsNullOrWhiteSpace(finding.Evidence)) text.Append("<p><b>Evidence:</b> ").Append(H(finding.Evidence)).Append("</p>");
            text.Append("</article>");
        }
        text.Append("<h2>Unit outcomes</h2><ul>");
        foreach (var observation in report.Observations) text.Append("<li><code>").Append(H(observation.Path)).Append("</code> · ").Append(H(observation.Outcome)).Append(observation.ProducedByRun ? " · produced" : " · reused/not produced").Append("</li>");
        text.Append("</ul><h2>Usage and provenance</h2><p>").Append(report.Execution.Usage.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable").Append(" input tokens · ").Append(report.Execution.Usage.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable").Append(" output tokens · ").Append(report.Execution.Usage.DurationMs).Append(" ms</p><p class=\"muted\">Repository ").Append(H(run.RepositoryName)).Append(" (<code>").Append(H(run.RepositoryId)).Append("</code>) · model ").Append(H(run.Model ?? "runner-default")).Append(" · thinking ").Append(H(run.ThinkingLevel ?? "model-default")).Append(" · CLI ").Append(H(run.CliType)).Append("</p></main></body></html>\n");
        return text.ToString();
    }

    private static string Sarif(QualityRunReportDocument report)
    {
        var findings = Active(report).ToArray();
        var rules = findings.GroupBy(finding => finding.RuleId, StringComparer.Ordinal).Select(group =>
        {
            var first = group.First();
            return new JsonObject
            {
                ["id"] = first.RuleId,
                ["name"] = SarifName(first.RuleId),
                ["shortDescription"] = new JsonObject { ["text"] = first.Title },
                ["help"] = new JsonObject { ["text"] = first.Recommendation, ["markdown"] = first.Recommendation },
            };
        }).ToArray();
        var results = findings.Select(finding =>
        {
            var result = new JsonObject
            {
                ["ruleId"] = finding.RuleId,
                ["level"] = SarifLevel(finding.Severity),
                ["message"] = new JsonObject { ["text"] = $"{finding.Title}: {finding.Description}" },
                ["partialFingerprints"] = new JsonObject
                {
                    ["qualityStudioFingerprint/v1"] = finding.Fingerprint,
                    ["primaryLocationLineHash"] = finding.Fingerprint,
                },
                ["properties"] = new JsonObject
                {
                    ["kind"] = report.Run.Kind,
                    ["severity"] = finding.Severity,
                    ["state"] = finding.State,
                    ["reviewRunId"] = report.Run.Id,
                    ["producedByRun"] = report.Observations.Any(observation => observation.ProducedByRun && observation.Findings.Any(candidate => candidate.Fingerprint == finding.Fingerprint)),
                    ["source"] = finding.Source,
                },
                ["locations"] = new JsonArray(finding.Locations.Select(location =>
                {
                    var physical = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = SarifUri(location.Path) } };
                    if (location.StartLine.HasValue)
                    {
                        var region = new JsonObject { ["startLine"] = Math.Max(1, location.StartLine.Value) };
                        if (location.StartColumn.HasValue) region["startColumn"] = Math.Max(1, location.StartColumn.Value);
                        if (location.EndLine.HasValue) region["endLine"] = Math.Max(region["startLine"]!.GetValue<int>(), location.EndLine.Value);
                        if (location.EndColumn.HasValue) region["endColumn"] = Math.Max(1, location.EndColumn.Value);
                        physical["region"] = region;
                    }
                    return new JsonObject { ["physicalLocation"] = physical };
                }).ToArray()),
            };
            if (finding.State is "waived" or "false-positive")
                result["suppressions"] = new JsonArray(new JsonObject { ["kind"] = "external", ["status"] = "accepted", ["justification"] = $"Quality Studio finding state: {finding.State}." });
            if (report.Delta.Status == "available")
                result["baselineState"] = report.Delta.New.Contains(finding.Fingerprint, StringComparer.Ordinal)
                    ? "new"
                    : report.Delta.StateChanged.Contains(finding.Fingerprint, StringComparer.Ordinal) ? "updated" : "unchanged";
            return result;
        }).ToArray();
        var category = "quality-studio/" + Uri.EscapeDataString(report.Run.RepositoryId) + "/" +
                       report.Run.Kind + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                           report.Run.Scope.UnitId + "\0" + report.Run.Scope.Level)))[..16] + "/";
        var sarif = new JsonObject
        {
            ["$schema"] = SarifSchema,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "Quality Studio",
                        ["informationUri"] = "https://agent-orchestrator.dev/quality",
                        ["semanticVersion"] = "1.0.0",
                        ["rules"] = new JsonArray(rules),
                    }
                },
                ["automationDetails"] = new JsonObject { ["id"] = category },
                ["invocations"] = new JsonArray(new JsonObject
                {
                    ["executionSuccessful"] = report.Run.State == "done",
                    ["endTimeUtc"] = report.Run.FinishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    ["properties"] = new JsonObject { ["reviewRunId"] = report.Run.Id, ["revision"] = report.Run.Revision, ["completeness"] = report.Run.Completeness },
                }),
                ["results"] = new JsonArray(results),
                ["properties"] = new JsonObject
                {
                    ["qualityRunReportSchema"] = report.Schema,
                    ["qualityRunReportSchemaVersion"] = report.SchemaVersion,
                    ["reviewRunId"] = report.Run.Id,
                    ["revision"] = report.Run.Revision,
                    ["repositoryId"] = report.Run.RepositoryId,
                    ["kind"] = report.Run.Kind,
                    ["scopeUnitId"] = report.Run.Scope.UnitId,
                    ["scopeLevel"] = report.Run.Scope.Level,
                    ["locationlessResults"] = findings.Count(finding => finding.Locations.Count == 0),
                },
            }),
        };
        return sarif.ToJsonString(QualityRunReportJson.Options) + Environment.NewLine;
    }

    private static IEnumerable<QualityRunFinding> Active(QualityRunReportDocument report) =>
        report.Observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .Select(group => group.First());

    private static string JoinSeverity(IReadOnlyDictionary<string, int> counts) =>
        string.Join(" · ", counts.Where(pair => pair.Value > 0).Select(pair => $"{pair.Value} {pair.Key}"));

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static int SeverityOrder(string severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => 4,
    };

    private static string SarifLevel(string severity) => severity switch
    {
        "critical" or "high" => "error",
        "medium" => "warning",
        _ => "note",
    };

    private static string SarifName(string ruleId)
    {
        var name = new string(ruleId.Select(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '_').ToArray());
        return name.Length == 0 || name[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_')
            ? "rule_" + name
            : name;
    }

    private static string SarifUri(string path) => string.Join('/',
        path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}

public static class QualityRunReportGate
{
    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0,
            ["high"] = 1,
            ["medium"] = 2,
            ["low"] = 3,
            ["info"] = 4,
        };

    public static IReadOnlyList<string> Evaluate(
        QualityRunReportDocument report,
        int? failUnder = null,
        string? failOnSeverity = null)
    {
        if (failUnder is < 0 or > 100) throw new ArgumentException("--fail-under must be between 0 and 100.");
        if (failOnSeverity is not null && !SeverityRank.ContainsKey(failOnSeverity))
            throw new ArgumentException("--fail-on must be critical, high, medium, low, or info.");
        var failures = new List<string>();
        if (failUnder.HasValue && (!report.Summary.Score.HasValue || report.Summary.Score.Value < failUnder.Value))
            failures.Add(report.Summary.Score.HasValue
                ? $"{report.Run.Id}: score {report.Summary.Score.Value} is below {failUnder.Value}"
                : $"{report.Run.Id}: score is unavailable for this partial run");
        if (failOnSeverity is not null)
        {
            var threshold = SeverityRank[failOnSeverity];
            var blocking = report.Observations.SelectMany(observation => observation.Findings)
                .Where(finding => finding.State is "open" or "accepted" &&
                                  SeverityRank.TryGetValue(finding.Severity, out var rank) && rank <= threshold)
                .Select(finding => finding.Fingerprint).Distinct(StringComparer.Ordinal).Count();
            if (blocking > 0) failures.Add($"{report.Run.Id}: {blocking} active finding(s) at {failOnSeverity} or higher");
        }
        return failures;
    }
}
