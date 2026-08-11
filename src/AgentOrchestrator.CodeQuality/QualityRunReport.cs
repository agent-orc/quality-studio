using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ReviewObservationSnapshot(
    string SidecarPath,
    string SidecarSha256,
    ReviewMetaDocument Document,
    IReadOnlyDictionary<string, string> FindingStates);

public sealed record QualityRunReportDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int SchemaVersion,
    int Revision,
    QualityRunIdentity Run,
    QualityRunSubject Subject,
    QualityRunExecution Execution,
    IReadOnlyList<QualityRunObservation> Observations,
    QualityRunDelta Delta,
    QualityRunSummary Summary);

public sealed record QualityRunIdentity(
    string Id,
    string RepositoryId,
    string RepositoryName,
    string Kind,
    QualityRunScope Scope,
    string State,
    string Completeness,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string Model,
    string ThinkingLevel,
    string CliType,
    bool Force,
    string? PartialReason);

public sealed record QualityRunScope(string UnitId, string Level, string Path);

public sealed record QualityRunSubject(IReadOnlyList<QualityRunTarget> Targets, string ManifestHash);

public sealed record QualityRunTarget(string UnitId, string Path, string SubjectHash);

public sealed record QualityRunExecution(
    string Outcome,
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    int Cancelled,
    int UsageOperations,
    TokenUsage Usage,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    long? TokenCap,
    decimal? CostCap,
    IReadOnlyList<string> Errors);

public sealed record QualityRunObservation(
    string UnitId,
    string Path,
    string Level,
    string Outcome,
    bool ProducedByRun,
    string? SidecarPath,
    string? SidecarSha256,
    string? ReviewedHash,
    string? ProviderRunId,
    DateTimeOffset? ReviewedAt,
    ReviewGrade? Grade,
    string? Summary,
    IReadOnlyList<QualityFinding> Findings,
    IReadOnlyList<QualityRunEvidence> DeterministicEvidence,
    string? Error = null);

public sealed record QualityRunEvidence(
    string SensorId,
    string SensorVersion,
    bool Available,
    string? UnavailableReason,
    string? ResultHash,
    int FindingCount,
    IReadOnlyDictionary<string, string> ToolVersions);

public sealed record QualityRunDelta(
    string Status,
    string? PriorRunId,
    IReadOnlyList<string> New,
    IReadOnlyList<string> Persisting,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> StateChanged);

public sealed record QualityRunSummary(
    int? Score,
    string? Grade,
    int ActiveFindings,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByState,
    string? HighestSeverity,
    string? PartialReason);

public sealed record QualityRunTrendPoint(
    string RunId,
    int Revision,
    DateTimeOffset? FinishedAt,
    string State,
    string Completeness,
    string Model,
    string CliType,
    int? Score,
    string? Grade,
    string? ScoreUnavailableReason,
    int ActiveFindings,
    QualityRunDelta Delta,
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    long? InputTokens,
    long? OutputTokens,
    long DurationMs,
    decimal? CostSpent,
    string? Currency,
    bool ConnectScore);

public sealed record QualityRunTrendPage(
    string RepositoryId,
    string Kind,
    QualityRunScope Scope,
    int Page,
    int PageSize,
    int Total,
    IReadOnlyList<QualityRunTrendPoint> Points);

public sealed class QualityRunReportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class QualityRunReportStore
{
    public const string SchemaId = "https://agent-orchestrator.dev/quality/schemas/quality-run-report.v1.schema.json";
    public const string RelativeReportsPath = ".quality/reports/runs";
    private static readonly UTF8Encoding Utf8 = new(false);
    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string ReportPath(string repositoryRoot, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ValidateRunId(runId);
        return Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeReportsPath.Replace('/', Path.DirectorySeparatorChar), runId + ".json");
    }

    public static void Save(string repositoryRoot, QualityRunReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(report.Schema, SchemaId, StringComparison.Ordinal) || report.SchemaVersion != 1)
            throw new QualityRunReportException("Only quality-run-report.v1 documents can be stored.");
        var destination = ReportPath(repositoryRoot, report.Run.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $"{report.Run.Id}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
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

    public static QualityRunReportDocument Load(string repositoryRoot, string runId)
    {
        var path = ReportPath(repositoryRoot, runId);
        if (!File.Exists(path)) throw new FileNotFoundException($"Review run report '{runId}' was not found.", path);
        try
        {
            return Read(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new QualityRunReportException($"Review run report '{runId}' is invalid.", exception);
        }
    }

    public static QualityRunReportDocument? TryLoad(string repositoryRoot, string runId) =>
        File.Exists(ReportPath(repositoryRoot, runId)) ? Load(repositoryRoot, runId) : null;

    public static IReadOnlyList<QualityRunReportDocument> LoadAll(string repositoryRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeReportsPath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return [];
        var reports = new List<QualityRunReportDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                reports.Add(Read(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new QualityRunReportException(
                    $"Review run report '{Path.GetFileNameWithoutExtension(path)}' is invalid.", exception);
            }
        }
        return reports;
    }

    private static QualityRunReportDocument Read(string path)
    {
        var report = JsonSerializer.Deserialize<QualityRunReportDocument>(File.ReadAllText(path), JsonOptions)
                     ?? throw new JsonException("Review run report must be a JSON object.");
        if (!string.Equals(report.Schema, SchemaId, StringComparison.Ordinal) || report.SchemaVersion != 1 ||
            report.Revision < 1 || !string.Equals(report.Run.Id, Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal))
            throw new JsonException("Review run report identity or schema is invalid.");
        return report;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            runId.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A review run id cannot contain path separators or an extension.", nameof(runId));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Default,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class QualityRunReportRenderer
{
    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3, ["info"] = 4,
        };

    public static string Render(QualityRunReportDocument report, QualityReportFormat format) => format switch
    {
        QualityReportFormat.Markdown => Markdown(report),
        QualityReportFormat.Html => Html(report),
        QualityReportFormat.Json => JsonSerializer.Serialize(report, QualityRunReportStore.JsonOptions) + Environment.NewLine,
        QualityReportFormat.Sarif => Sarif(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string Markdown(QualityRunReportDocument report)
    {
        var text = new StringBuilder();
        var run = report.Run;
        text.AppendLine("# Quality Studio review run");
        text.AppendLine();
        text.AppendLine($"**{Escape(run.State.ToUpperInvariant())} · {Escape(run.Kind)} · {Escape(run.Scope.Level)} `{Escape(run.Scope.Path)}` · {Escape(run.Completeness)}**");
        text.AppendLine();
        text.AppendLine($"Run `{Escape(run.Id)}` · {report.Subject.Targets.Count} targets · {report.Execution.Reviewed} reviewed · {report.Execution.ReusedFresh} reused · {report.Execution.Failed} failed");
        if (report.Summary.Score.HasValue)
            text.AppendLine($"Score {report.Summary.Score}/100 ({Escape(report.Summary.Grade ?? string.Empty)}) · {report.Summary.ActiveFindings} active findings · {Counts(report.Summary.BySeverity)}");
        else
            text.AppendLine($"Score unavailable ({Escape(report.Summary.PartialReason ?? "incomplete run")}) · {report.Summary.ActiveFindings} active findings");
        text.AppendLine(report.Delta.Status == "available"
            ? $"Delta from `{Escape(report.Delta.PriorRunId ?? string.Empty)}`: {report.Delta.New.Count} new · {report.Delta.Persisting.Count} persisting · {report.Delta.Resolved.Count} resolved · {report.Delta.StateChanged.Count} state-changed"
            : "Delta: unavailable (no prior comparable complete run snapshot)");

        var active = ActiveFindings(report).Take(20).ToArray();
        if (active.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Findings");
            text.AppendLine();
            for (var index = 0; index < active.Length; index++)
            {
                var finding = active[index];
                var location = finding.Locations.FirstOrDefault();
                var at = location is null ? "." : location.Path + (location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty);
                text.AppendLine($"{index + 1}. [{Escape(finding.Severity)}/{Escape(finding.State)}] {Escape(finding.Title)}");
                text.AppendLine($"   {Escape(at)} · {Escape(finding.RuleId)}");
            }
        }
        var omitted = Math.Max(0, ActiveFindings(report).Count - active.Length);
        if (omitted > 0)
        {
            text.AppendLine();
            text.AppendLine($"{omitted} additional active finding(s) omitted; use JSON or SARIF for the full set.");
        }
        return text.ToString();
    }

    private static string Html(QualityRunReportDocument report)
    {
        static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var active = ActiveFindings(report);
        var text = new StringBuilder();
        text.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        text.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; base-uri 'none'; form-action 'none'\">");
        text.Append("<title>Quality Studio review run ").Append(H(report.Run.Id)).Append("</title><style>");
        text.Append(":root{color-scheme:light dark;--bg:#fcfcfb;--panel:#f2f1ee;--ink:#171717;--muted:#62615d;--line:#d8d6cf} @media(prefers-color-scheme:dark){:root{--bg:#1a1a19;--panel:#242423;--ink:#f5f5f2;--muted:#b8b7af;--line:#41413d}} *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,sans-serif}main{max-width:70rem;margin:auto;padding:2rem 1.5rem 4rem}header{border-bottom:1px solid var(--line);padding-bottom:1.25rem}.kicker,.muted{color:var(--muted)}h1{font-size:2rem;margin:.25rem 0}.summary{display:grid;grid-template-columns:repeat(auto-fit,minmax(10rem,1fr));gap:.75rem;margin:1.5rem 0}.card{background:var(--panel);border:1px solid var(--line);border-radius:.6rem;padding:1rem}.card b{display:block;font-size:1.35rem}table{width:100%;border-collapse:collapse}th,td{text-align:left;vertical-align:top;border-bottom:1px solid var(--line);padding:.65rem}.finding{padding:1rem 0;border-bottom:1px solid var(--line)}code{overflow-wrap:anywhere}@media print{main{max-width:none;padding:0}.card{break-inside:avoid}}@media(max-width:40rem){th:nth-child(3),td:nth-child(3){display:none}}");
        text.Append("</style></head><body><main><header><p class=\"kicker\">Quality Studio · review run</p><h1>").Append(H(report.Run.RepositoryName)).Append("</h1><p><b>")
            .Append(H(report.Run.State.ToUpperInvariant())).Append("</b> · ").Append(H(report.Run.Kind)).Append(" · ")
            .Append(H(report.Run.Scope.Level)).Append(" <code>").Append(H(report.Run.Scope.Path)).Append("</code> · ")
            .Append(H(report.Run.Completeness)).Append("</p><p class=\"muted\">Run <code>").Append(H(report.Run.Id)).Append("</code> · revision ")
            .Append(report.Revision.ToString(CultureInfo.InvariantCulture)).Append("</p></header><section class=\"summary\">");
        AddCard(text, "Score", report.Summary.Score?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable", report.Summary.Grade);
        AddCard(text, "Active findings", report.Summary.ActiveFindings.ToString(CultureInfo.InvariantCulture), report.Summary.HighestSeverity);
        AddCard(text, "Reviewed / reused", $"{report.Execution.Reviewed} / {report.Execution.ReusedFresh}", null);
        AddCard(text, "Failed / skipped", $"{report.Execution.Failed} / {report.Execution.Skipped}", null);
        text.Append("</section><h2>Finding movement</h2><p>");
        text.Append(report.Delta.Status == "available"
            ? $"{report.Delta.New.Count} new · {report.Delta.Persisting.Count} persisting · {report.Delta.Resolved.Count} resolved · {report.Delta.StateChanged.Count} state-changed"
            : "Unavailable — no prior comparable complete run.");
        text.Append("</p><h2>Findings</h2>");
        if (active.Count == 0) text.Append("<p>No active findings.</p>");
        foreach (var finding in active)
        {
            var location = finding.Locations.FirstOrDefault();
            text.Append("<article class=\"finding\"><p><b>").Append(H(finding.Title)).Append("</b><br><span class=\"muted\">")
                .Append(H(finding.Severity)).Append(" · ").Append(H(finding.State)).Append(" · ").Append(H(finding.RuleId)).Append("</span></p><p>")
                .Append(H(finding.Description)).Append("</p><p><b>Recommendation:</b> ").Append(H(finding.Recommendation)).Append("</p>");
            if (location is not null) text.Append("<p><code>").Append(H(location.Path)).Append(location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty).Append("</code></p>");
            text.Append("</article>");
        }
        text.Append("<h2>Unit outcomes</h2><table><thead><tr><th>Unit</th><th>Outcome</th><th>Grade</th></tr></thead><tbody>");
        foreach (var observation in report.Observations)
            text.Append("<tr><td><code>").Append(H(observation.Path)).Append("</code></td><td>").Append(H(observation.Outcome))
                .Append("</td><td>").Append(observation.Grade?.Score.ToString(CultureInfo.InvariantCulture) ?? "—").Append("</td></tr>");
        text.Append("</tbody></table><h2>Provenance</h2><p>Model ").Append(H(report.Run.Model)).Append(" · ").Append(H(report.Run.ThinkingLevel))
            .Append(" · CLI ").Append(H(report.Run.CliType)).Append("</p></main></body></html>\n");
        return text.ToString();
    }

    private static string Sarif(QualityRunReportDocument report)
    {
        var findings = ActiveFindings(report);
        var rules = findings.GroupBy(finding => finding.RuleId, StringComparer.Ordinal).Select(group =>
        {
            var finding = group.First();
            return new JsonObject
            {
                ["id"] = finding.RuleId,
                ["name"] = SarifName(finding.RuleId),
                ["shortDescription"] = new JsonObject { ["text"] = finding.Title },
                ["help"] = new JsonObject { ["text"] = finding.Recommendation, ["markdown"] = finding.Recommendation },
            };
        }).ToArray();
        var hasBaseline = report.Run.State == "done" && report.Run.Completeness == "complete" && report.Delta.Status == "available";
        var results = findings.Select(finding =>
        {
            var locations = finding.Locations.Select(location =>
            {
                var physical = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = SarifUri(location.Path) },
                };
                if (location.StartLine.HasValue)
                {
                    var region = new JsonObject { ["startLine"] = Math.Max(1, location.StartLine.Value) };
                    if (location.StartColumn.HasValue) region["startColumn"] = Math.Max(1, location.StartColumn.Value);
                    if (location.EndLine.HasValue) region["endLine"] = Math.Max(region["startLine"]!.GetValue<int>(), location.EndLine.Value);
                    if (location.EndColumn.HasValue) region["endColumn"] = Math.Max(1, location.EndColumn.Value);
                    physical["region"] = region;
                }
                return new JsonObject { ["physicalLocation"] = physical };
            }).ToArray();
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
                ["locations"] = new JsonArray(locations),
                ["properties"] = new JsonObject
                {
                    ["kind"] = finding.Kind, ["severity"] = finding.Severity, ["state"] = finding.State,
                    ["recommendation"] = finding.Recommendation, ["source"] = finding.Source,
                    ["sensorId"] = finding.SensorId, ["producer"] = finding.Producer,
                },
            };
            if (hasBaseline)
            {
                result["baselineState"] = report.Delta.New.Contains(finding.Fingerprint, StringComparer.Ordinal) ? "new" :
                    report.Delta.StateChanged.Contains(finding.Fingerprint, StringComparer.Ordinal) ? "updated" : "unchanged";
            }
            if (finding.State is "waived" or "false-positive")
                result["suppressions"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "external", ["status"] = "accepted",
                    ["justification"] = $"Quality Studio finding state: {finding.State}.",
                });
            return result;
        }).ToArray();
        var run = new JsonObject
        {
            ["tool"] = new JsonObject { ["driver"] = new JsonObject
            {
                ["name"] = "Quality Studio", ["informationUri"] = "https://agent-orchestrator.dev/quality",
                ["semanticVersion"] = "1.0.0", ["rules"] = new JsonArray(rules),
            } },
            ["automationDetails"] = new JsonObject
            {
                ["id"] = $"quality-studio/{report.Run.RepositoryId}/{report.Run.Kind}/{report.Run.Scope.Level}/{report.Subject.ManifestHash}/",
            },
            ["invocations"] = new JsonArray(new JsonObject
            {
                ["executionSuccessful"] = report.Run.State == "done",
                ["properties"] = new JsonObject { ["state"] = report.Run.State, ["completeness"] = report.Run.Completeness },
            }),
            ["results"] = new JsonArray(results),
            ["properties"] = new JsonObject
            {
                ["qualityRunReportSchema"] = report.Schema,
                ["qualityRunReportSchemaVersion"] = report.SchemaVersion,
                ["reviewRunId"] = report.Run.Id,
                ["revision"] = report.Revision,
                ["repositoryId"] = report.Run.RepositoryId,
                ["scopeUnitId"] = report.Run.Scope.UnitId,
                ["summary"] = JsonSerializer.SerializeToNode(report.Summary, QualityRunReportStore.JsonOptions),
                ["execution"] = JsonSerializer.SerializeToNode(report.Execution, QualityRunReportStore.JsonOptions),
                ["delta"] = JsonSerializer.SerializeToNode(report.Delta, QualityRunReportStore.JsonOptions),
            },
        };
        return new JsonObject
        {
            ["$schema"] = QualityReportRenderer.SarifSchema,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(run),
        }.ToJsonString(QualityRunReportStore.JsonOptions) + Environment.NewLine;
    }

    private static IReadOnlyList<QualityFinding> ActiveFindings(QualityRunReportDocument report) =>
        report.Observations.SelectMany(observation => observation.Findings)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .Where(finding => finding.State != "resolved")
            .OrderBy(finding => SeverityRank.GetValueOrDefault(finding.Severity, int.MaxValue))
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();

    private static void AddCard(StringBuilder text, string label, string value, string? detail)
    {
        text.Append("<div class=\"card\"><span class=\"muted\">").Append(WebUtility.HtmlEncode(label)).Append("</span><b>")
            .Append(WebUtility.HtmlEncode(value)).Append("</b>");
        if (!string.IsNullOrWhiteSpace(detail)) text.Append("<span>").Append(WebUtility.HtmlEncode(detail)).Append("</span>");
        text.Append("</div>");
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        string.Join(" · ", counts.Where(pair => pair.Value > 0).Select(pair => $"{pair.Value} {pair.Key}"));

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string SarifLevel(string severity) => severity switch
    {
        "critical" or "high" => "error", "medium" => "warning", _ => "note",
    };

    private static string SarifName(string ruleId)
    {
        var name = new string(ruleId.Select(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ? character : '_').ToArray());
        return name.Length > 0 && name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' ? name : "rule_" + name;
    }

    private static string SarifUri(string path) => string.Join('/',
        path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}

public static class QualityRunReportGate
{
    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["low"] = 3, ["info"] = 4,
        };

    public static IReadOnlyList<string> Evaluate(
        QualityRunReportDocument report, int? failUnder = null, string? failOnSeverity = null)
    {
        if (failUnder is < 0 or > 100) throw new ArgumentException("--fail-under must be between 0 and 100.");
        if (failOnSeverity is not null && !SeverityRank.ContainsKey(failOnSeverity))
            throw new ArgumentException("--fail-on must be critical, high, medium, low, or info.");
        var failures = new List<string>();
        if (failUnder.HasValue)
        {
            if (!report.Summary.Score.HasValue) failures.Add($"{report.Run.Id}: score is unavailable for a partial run");
            else if (report.Summary.Score.Value < failUnder.Value)
                failures.Add($"{report.Run.Id}: score {report.Summary.Score.Value} is below {failUnder.Value}");
        }
        if (failOnSeverity is not null)
        {
            var threshold = SeverityRank[failOnSeverity];
            var blocking = report.Observations.SelectMany(observation => observation.Findings)
                .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .Count(finding => finding.State is "open" or "accepted" &&
                                  SeverityRank.TryGetValue(finding.Severity, out var rank) && rank <= threshold);
            if (blocking > 0) failures.Add($"{report.Run.Id}: {blocking} active finding(s) at {failOnSeverity} or higher");
        }
        return failures;
    }
}

public static class QualityRunTrendBuilder
{
    public static QualityRunTrendPage Build(
        string repositoryRoot, QualityRunReportDocument reference, int page = 1, int pageSize = 30)
    {
        if (page < 1) throw new ArgumentException("Trend page must be at least 1.", nameof(page));
        if (pageSize is < 1 or > 100) throw new ArgumentException("Trend page size must be between 1 and 100.", nameof(pageSize));
        var selected = QualityRunReportStore.LoadAll(repositoryRoot)
            .Where(report => SameSeries(report, reference))
            .GroupBy(report => report.Run.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(report => report.Revision).First())
            .OrderByDescending(report => report.Run.FinishedAt ?? report.Run.CreatedAt)
            .ThenByDescending(report => report.Run.Id, StringComparer.Ordinal)
            .ToArray();
        var points = selected.Skip((page - 1) * pageSize).Take(pageSize).Select(report => new QualityRunTrendPoint(
            report.Run.Id, report.Revision, report.Run.FinishedAt, report.Run.State, report.Run.Completeness,
            report.Run.Model, report.Run.CliType, report.Summary.Score, report.Summary.Grade,
            report.Summary.Score.HasValue ? null : report.Summary.PartialReason,
            report.Summary.ActiveFindings, report.Delta, report.Execution.Reviewed, report.Execution.ReusedFresh,
            report.Execution.Failed, report.Execution.Skipped, report.Execution.Usage.InputTokens,
            report.Execution.Usage.OutputTokens, report.Execution.Usage.DurationMs, report.Execution.CostSpent,
            report.Execution.Currency,
            report.Run.State == "done" && report.Run.Completeness == "complete" && report.Summary.Score.HasValue)).ToArray();
        return new QualityRunTrendPage(reference.Run.RepositoryId, reference.Run.Kind, reference.Run.Scope,
            page, pageSize, selected.Length, points);
    }

    public static bool SameSeries(QualityRunReportDocument left, QualityRunReportDocument right) =>
        string.Equals(left.Run.RepositoryId, right.Run.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Run.Kind, right.Run.Kind, StringComparison.Ordinal) &&
        string.Equals(left.Run.Scope.UnitId, right.Run.Scope.UnitId, StringComparison.Ordinal) &&
        string.Equals(left.Run.Scope.Level, right.Run.Scope.Level, StringComparison.Ordinal);
}

public static class QualityRunReportFingerprint
{
    public static string Manifest(IEnumerable<QualityRunTarget> targets)
    {
        var canonical = string.Join('\n', targets.Select(target =>
            string.Join('\0', target.UnitId, target.Path.Replace('\\', '/'), target.SubjectHash)));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("quality-studio-run-subject-v1\0" + canonical)));
    }
}
