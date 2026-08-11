using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public static class QualityRunReportRenderer
{
    public const int MarkdownFindingLimit = 20;

    private static readonly IReadOnlyDictionary<string, int> SeverityRank =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = 0,
            ["high"] = 1,
            ["medium"] = 2,
            ["low"] = 3,
            ["info"] = 4,
        };

    public static string Render(QualityRunReportDocument report, QualityReportFormat format) => format switch
    {
        QualityReportFormat.Markdown => Markdown(report),
        QualityReportFormat.Html => Html(report),
        QualityReportFormat.Json => QualityRunReportJson.Serialize(report),
        QualityReportFormat.Sarif => Sarif(report),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string FileExtension(QualityReportFormat format) => format switch
    {
        QualityReportFormat.Markdown => "md",
        QualityReportFormat.Html => "html",
        QualityReportFormat.Json => "json",
        QualityReportFormat.Sarif => "sarif",
        _ => "txt",
    };

    private static string Markdown(QualityRunReportDocument report)
    {
        var text = new StringBuilder();
        var run = report.Run;
        var summary = report.Summary;
        text.AppendLine("# Quality Studio review run");
        text.AppendLine();
        text.AppendLine($"**{run.State.ToUpperInvariant()} · {EscapeMarkdown(run.Kind)} · {EscapeMarkdown(run.Level)} `{EscapeMarkdown(run.Path)}` · {run.Completeness}**");
        text.AppendLine();
        text.AppendLine($"Run `{EscapeMarkdown(run.Id)}` revision {run.Revision} · {report.Subject.Targets.Count} targets · {report.Execution.Reviewed} reviewed · {report.Execution.ReusedFresh} reused · {report.Execution.Failed} failed · {report.Execution.Skipped} skipped");
        text.AppendLine();
        text.AppendLine(summary.Score.HasValue
            ? $"Score {summary.Score}/100 ({summary.Grade}) · {summary.Findings.Total} active findings · {JoinCounts(summary.Findings.BySeverity)}"
            : $"Score unavailable · {summary.Findings.Total} active findings · {EscapeMarkdown(summary.PartialReason ?? "partial run")}");
        text.AppendLine(report.Delta.Status == "available"
            ? $"Delta from `{EscapeMarkdown(report.Delta.PriorRunId!)}`: {report.Delta.New.Count} new · {report.Delta.Persisting.Count} persisting · {report.Delta.Resolved.Count} resolved · {report.Delta.StateChanged.Count} state-changed"
            : $"Delta: unavailable ({EscapeMarkdown(report.Delta.Reason ?? "no prior comparable run snapshot")})");

        var findings = ActiveFindings(report)
            .OrderBy(finding => SeverityRank.GetValueOrDefault(finding.Severity, int.MaxValue))
            .ThenBy(finding => finding.Locations.FirstOrDefault()?.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Title, StringComparer.Ordinal)
            .ToArray();
        if (findings.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("## Active findings");
            text.AppendLine();
            foreach (var pair in findings.Take(MarkdownFindingLimit).Select((finding, index) => (finding, index)))
            {
                var location = pair.finding.Locations.FirstOrDefault();
                var at = location is null
                    ? "location unavailable"
                    : location.Path + (location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty);
                text.AppendLine($"{pair.index + 1}. [{EscapeMarkdown(pair.finding.Severity)}/{EscapeMarkdown(pair.finding.State)}] {EscapeMarkdown(pair.finding.Title)}");
                text.AppendLine($"   {EscapeMarkdown(at)} · {EscapeMarkdown(pair.finding.RuleId)}");
            }
            if (findings.Length > MarkdownFindingLimit)
            {
                text.AppendLine();
                text.AppendLine($"{findings.Length - MarkdownFindingLimit} additional active finding(s) omitted; use JSON or SARIF for the complete result.");
            }
        }

        text.AppendLine();
        text.AppendLine("## Unit outcomes");
        text.AppendLine();
        foreach (var observation in report.Observations)
            text.AppendLine($"- {EscapeMarkdown(observation.Outcome)} · {EscapeMarkdown(observation.Level)} · {EscapeMarkdown(observation.Path)}{(observation.ProducedByRun ? " · produced" : observation.Outcome == "skipped-fresh" ? " · reused" : string.Empty)}");

        text.AppendLine();
        text.AppendLine("## Usage and provenance");
        text.AppendLine();
        text.AppendLine($"- Route: {EscapeMarkdown(run.CliType)} · {EscapeMarkdown(run.Model)} · {EscapeMarkdown(run.ThinkingLevel)}");
        text.AppendLine($"- Usage: {Number(report.Execution.Usage.InputTokens)} input · {Number(report.Execution.Usage.OutputTokens)} output · {report.Execution.Usage.DurationMs} ms · {Cost(report.Execution.Usage)}");
        if (report.Execution.Cap.Reason is not null)
            text.AppendLine($"- Cap: {EscapeMarkdown(report.Execution.Cap.Outcome)} · {EscapeMarkdown(report.Execution.Cap.Reason)}");
        return text.ToString();
    }

    private static string Html(QualityRunReportDocument report)
    {
        var run = report.Run;
        var summary = report.Summary;
        var findings = CurrentFindings(report)
            .OrderBy(finding => SeverityRank.GetValueOrDefault(finding.Severity, int.MaxValue))
            .ThenBy(finding => finding.Title, StringComparer.Ordinal).ToArray();
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; img-src data:; object-src 'none'; base-uri 'none'; form-action 'none'\">");
        html.Append("<title>Quality Studio review run ").Append(H(run.Id)).Append("</title><style>");
        html.Append(" :root{color-scheme:light dark;--bg:#fbfbfa;--surface:#f2f2ef;--ink:#171715;--muted:#62625d;--line:#d8d7d1;--ok:#19733a;--warn:#936300;--bad:#a53333} @media(prefers-color-scheme:dark){:root{--bg:#191918;--surface:#242423;--ink:#f7f7f4;--muted:#b5b4ad;--line:#3e3e3a;--ok:#68c884;--warn:#e0b14e;--bad:#ef8585}}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.55 system-ui,sans-serif}main{max-width:72rem;margin:auto;padding:2.5rem 1.5rem 5rem}header{padding-bottom:1.5rem;border-bottom:1px solid var(--line)}h1{margin:.25rem 0;font-size:2rem;line-height:1.2}.eyebrow,.muted{color:var(--muted)}.eyebrow{text-transform:uppercase;letter-spacing:.1em;font-size:.75rem}.repo-line{display:flex;gap:.6rem;align-items:baseline;flex-wrap:wrap;margin:.75rem 0}.state,.outcome{display:inline-block;padding:.2rem .65rem;border:1px solid var(--line);border-radius:999px;font-size:.8rem;font-weight:700}.partial{color:var(--warn)}section{margin-top:2.5rem}h2{margin-bottom:1rem;font-size:1.25rem}.summary{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:1px;border:1px solid var(--line);background:var(--line)}.summary div{padding:1rem;background:var(--surface)}.summary b{display:block;font-size:1.25rem}.verdict{padding:1rem 0;border-bottom:1px solid var(--line)}.verdict header{display:flex;justify-content:space-between;gap:1rem;padding:0;border:0}.verdict h3{margin:0;font-size:1rem}.verdict p{max-width:76ch;margin:.5rem 0}.verdict-score{font-weight:700;white-space:nowrap}table{width:100%;border-collapse:collapse}th,td{padding:.65rem;text-align:left;vertical-align:top;border-bottom:1px solid var(--line)}th{width:12rem;color:var(--muted);font-size:.75rem;text-transform:uppercase}.finding{padding:1rem 0;border-bottom:1px solid var(--line)}.finding h3{margin:.35rem 0;font-size:1rem}.finding p{max-width:75ch}.severity{font-size:.75rem;font-weight:700;text-transform:uppercase}.critical,.high{color:var(--bad)}.medium{color:var(--warn)}code{font-family:ui-monospace,monospace;color:var(--muted);overflow-wrap:anywhere}.locations{display:flex;gap:.5rem;flex-wrap:wrap}@media(max-width:42rem){main{padding-inline:1rem}.summary{grid-template-columns:1fr 1fr}.verdict header{display:block}.verdict-score{display:block;margin-top:.4rem}th{width:8rem}}@media print{body{background:#fff;color:#000}main{max-width:none;padding:0}.state,.outcome{border-color:#777}section{break-inside:avoid}} ");
        html.Append("</style></head><body><main><header><div class=\"eyebrow\">Quality Studio · review run dossier</div><h1>")
            .Append(H(run.RepositoryName)).Append("</h1><div class=\"repo-line\"><strong>")
            .Append(H(run.RepositoryId)).Append("</strong><code>")
            .Append(H(run.RepositorySha ?? "repository SHA unavailable")).Append("</code></div><p class=\"muted\">Repository HEAD at enqueue · ")
            .Append(H(run.Kind)).Append(" · ").Append(H(run.Level)).Append(" · <code>").Append(H(run.Path))
            .Append("</code></p><span class=\"state ")
            .Append(run.Completeness == "partial" ? "partial" : string.Empty).Append("\">")
            .Append(H(run.State)).Append(" · ").Append(H(run.Completeness)).Append("</span><p class=\"muted\">Run <code>")
            .Append(H(run.Id)).Append("</code> · revision ").Append(run.Revision);
        if (run.FinishedAt.HasValue)
            html.Append(" · finished ").Append(H(run.FinishedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        html.Append("</p></header>");
        html.Append("<section><h2>Outcome summary</h2><div class=\"summary\"><div><b>")
            .Append(summary.Score?.ToString(CultureInfo.InvariantCulture) ?? "—").Append("</b><span>Score ")
            .Append(H(summary.Grade ?? "unavailable")).Append("</span></div><div><b>").Append(summary.Findings.Total)
            .Append("</b><span>Active findings</span></div><div><b>").Append(report.Execution.Reviewed)
            .Append("</b><span>Reviewed</span></div><div><b>").Append(report.Execution.ReusedFresh)
            .Append("</b><span>Reused fresh</span></div></div>");
        if (summary.PartialReason is not null) html.Append("<p class=\"partial\">").Append(H(summary.PartialReason)).Append("</p>");
        html.Append("<p class=\"muted\">Delta: ").Append(report.Delta.Status == "available"
            ? $"{report.Delta.New.Count} new · {report.Delta.Persisting.Count} persisting · {report.Delta.Resolved.Count} resolved · {report.Delta.StateChanged.Count} state-changed"
            : H(report.Delta.Reason ?? "unavailable")).Append("</p></section>");

        html.Append("<section><h2>Review verdicts</h2>");
        foreach (var observation in report.Observations)
        {
            html.Append("<article class=\"verdict\"><header><div><span class=\"outcome\">")
                .Append(H(observation.Outcome)).Append("</span><h3><code>").Append(H(observation.Path))
                .Append("</code></h3></div><span class=\"verdict-score\">");
            if (observation.Grade is null)
                html.Append("Verdict unavailable");
            else
                html.Append(observation.Grade.Score).Append(" / 100 · ").Append(H(observation.Grade.Band));
            html.Append("</span></header>");
            if (observation.Grade is not null)
                html.Append("<p>").Append(H(observation.Grade.Rationale)).Append("</p>");
            if (observation.Summary is not null)
                html.Append("<p class=\"muted\">").Append(H(observation.Summary)).Append("</p>");
            html.Append("<p class=\"muted\">").Append(observation.ProducedByRun ? "Produced by this run" : "Reused observation");
            if (observation.SidecarPath is not null)
                html.Append(" · <code>").Append(H(observation.SidecarPath)).Append("</code> · <code>")
                    .Append(H(observation.SidecarSha256 ?? "digest unavailable")).Append("</code>");
            html.Append("</p></article>");
        }
        html.Append("</section><section><h2>Findings</h2>");
        if (findings.Length == 0) html.Append("<p class=\"muted\">No active findings were captured.</p>");
        foreach (var finding in findings)
        {
            html.Append("<article class=\"finding\"><span class=\"severity ").Append(H(finding.Severity)).Append("\">")
                .Append(H(finding.Severity)).Append(" · ").Append(H(finding.State)).Append("</span><h3>")
                .Append(H(finding.Title)).Append("</h3><code>").Append(H(finding.RuleId)).Append("</code>");
            if (finding.Locations.Count > 0)
            {
                html.Append("<p class=\"locations\">");
                foreach (var location in finding.Locations)
                    html.Append("<code>").Append(H(location.Path))
                        .Append(location.StartLine.HasValue ? $":{location.StartLine}" : string.Empty).Append("</code>");
                html.Append("</p>");
            }
            html.Append("<p>").Append(H(finding.Description)).Append("</p><p><b>Recommendation:</b> ")
                .Append(H(finding.Recommendation)).Append("</p>");
            if (finding.Evidence is not null) html.Append("<p><b>Evidence:</b> ").Append(H(finding.Evidence)).Append("</p>");
            html.Append("<p class=\"muted\">Source: ").Append(H(finding.Source));
            if (finding.Producer is not null) html.Append(" · ").Append(H(finding.Producer));
            if (finding.SensorId is not null) html.Append(" · ").Append(H(finding.SensorId));
            html.Append("</p>");
            html.Append("</article>");
        }
        html.Append("</section><section><h2>Token ledger</h2><table><tbody><tr><th>Operations</th><td>")
            .Append(report.Execution.Usage.Operations).Append("</td></tr><tr><th>Input tokens</th><td>")
            .Append(H(Number(report.Execution.Usage.InputTokens))).Append("</td></tr><tr><th>Cached input</th><td>")
            .Append(H(Number(report.Execution.Usage.CachedInputTokens))).Append("</td></tr><tr><th>Output tokens</th><td>")
            .Append(H(Number(report.Execution.Usage.OutputTokens))).Append("</td></tr><tr><th>Reasoning output</th><td>")
            .Append(H(Number(report.Execution.Usage.ReasoningOutputTokens))).Append("</td></tr><tr><th>Elapsed</th><td>")
            .Append(report.Execution.Usage.DurationMs).Append(" ms</td></tr><tr><th>Cost</th><td>")
            .Append(H(Cost(report.Execution.Usage))).Append("</td></tr><tr><th>Cap</th><td>")
            .Append(H(report.Execution.Cap.Outcome));
        if (report.Execution.Cap.Reason is not null) html.Append(" · ").Append(H(report.Execution.Cap.Reason));
        html.Append("</td></tr></tbody></table></section><section><h2>Provenance</h2><table><tbody><tr><th>Route</th><td>")
            .Append(H(run.CliType)).Append(" · ").Append(H(run.Model)).Append(" · ").Append(H(run.ThinkingLevel))
            .Append("</td></tr><tr><th>Subject manifest</th><td><code>")
            .Append(H(report.Subject.ManifestHash)).Append("</code></td></tr></tbody></table></section></main></body></html>\n");
        return html.ToString();
    }

    private static string Sarif(QualityRunReportDocument report)
    {
        var findings = CurrentFindings(report).ToArray();
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
        var baselineAvailable = report.Run.Completeness == "complete" && report.Delta.Status == "available";
        var newFingerprints = report.Delta.New.ToHashSet(StringComparer.Ordinal);
        var changedFingerprints = report.Delta.StateChanged.ToHashSet(StringComparer.Ordinal);
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
                    ["severity"] = finding.Severity,
                    ["state"] = finding.State,
                    ["recommendation"] = finding.Recommendation,
                    ["source"] = finding.Source,
                    ["sensorId"] = finding.SensorId,
                    ["producer"] = finding.Producer,
                },
                ["locations"] = new JsonArray(finding.Locations.Select(location =>
                {
                    var physical = new JsonObject
                    {
                        ["artifactLocation"] = new JsonObject { ["uri"] = SarifUri(location.Path) },
                    };
                    if (location.StartLine.HasValue)
                    {
                        var region = new JsonObject { ["startLine"] = Math.Max(1, location.StartLine.Value) };
                        if (location.StartColumn.HasValue) region["startColumn"] = Math.Max(1, location.StartColumn.Value);
                        if (location.EndLine.HasValue) region["endLine"] = Math.Max(location.StartLine.Value, location.EndLine.Value);
                        if (location.EndColumn.HasValue) region["endColumn"] = Math.Max(1, location.EndColumn.Value);
                        physical["region"] = region;
                    }
                    return new JsonObject { ["physicalLocation"] = physical };
                }).ToArray()),
            };
            if (baselineAvailable)
                result["baselineState"] = newFingerprints.Contains(finding.Fingerprint) ? "new" :
                    changedFingerprints.Contains(finding.Fingerprint) ? "updated" : "unchanged";
            if (finding.State is "waived" or "false-positive")
                result["suppressions"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "external",
                    ["status"] = "accepted",
                    ["justification"] = $"Quality Studio finding state: {finding.State}.",
                });
            return result;
        }).ToArray();

        var scopeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{report.Run.ScopeUnitId}\0{report.Run.Level}")))[..16];
        var run = new JsonObject
        {
            ["tool"] = new JsonObject
            {
                ["driver"] = new JsonObject
                {
                    ["name"] = "Quality Studio",
                    ["informationUri"] = "https://agent-orchestrator.dev/quality",
                    ["semanticVersion"] = "1.0.0",
                    ["rules"] = new JsonArray(rules),
                },
            },
            ["automationDetails"] = new JsonObject
            {
                ["id"] = $"quality-studio/{Uri.EscapeDataString(report.Run.RepositoryId)}/{report.Run.Kind}/{scopeHash}/",
            },
            ["invocations"] = new JsonArray(new JsonObject
            {
                ["executionSuccessful"] = report.Run.State == "done",
                ["startTimeUtc"] = report.Run.StartedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["endTimeUtc"] = report.Run.FinishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["properties"] = new JsonObject
                {
                    ["reviewRunId"] = report.Run.Id,
                    ["reviewRunRevision"] = report.Run.Revision,
                    ["state"] = report.Run.State,
                    ["completeness"] = report.Run.Completeness,
                },
            }),
            ["results"] = new JsonArray(results),
            ["properties"] = new JsonObject
            {
                ["qualityRunReportSchema"] = report.Schema,
                ["qualityRunReportSchemaVersion"] = report.SchemaVersion,
                ["reviewRunId"] = report.Run.Id,
                ["reviewRunRevision"] = report.Run.Revision,
                ["repositoryId"] = report.Run.RepositoryId,
                ["kind"] = report.Run.Kind,
                ["scopeUnitId"] = report.Run.ScopeUnitId,
                ["scopeLevel"] = report.Run.Level,
                ["scopePath"] = report.Run.Path,
                ["state"] = report.Run.State,
                ["completeness"] = report.Run.Completeness,
                ["locationlessResults"] = findings.Count(finding => finding.Locations.Count == 0),
                ["summary"] = JsonSerializer.SerializeToNode(report.Summary, QualityRunReportJson.Options),
                ["delta"] = JsonSerializer.SerializeToNode(report.Delta, QualityRunReportJson.Options),
            },
        };
        var sarif = new JsonObject
        {
            ["$schema"] = QualityReportRenderer.SarifSchema,
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(run),
        };
        return sarif.ToJsonString(QualityRunReportJson.Options) + Environment.NewLine;
    }

    private static IEnumerable<QualityRunFinding> CurrentFindings(QualityRunReportDocument report) =>
        report.Observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal);

    private static IEnumerable<QualityRunFinding> ActiveFindings(QualityRunReportDocument report) =>
        CurrentFindings(report).Where(finding => finding.State is "open" or "accepted");

    private static string JoinCounts(IReadOnlyDictionary<string, int> counts) => string.Join(" · ",
        counts.Where(pair => pair.Value > 0).Select(pair => $"{pair.Value} {pair.Key}"));

    private static string Number(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unavailable";

    private static string Cost(QualityRunUsage usage) => usage.Cost.HasValue
        ? $"{usage.Cost:0.####} {usage.Currency ?? "USD"}"
        : $"cost {usage.PriceStatus}";

    private static string EscapeMarkdown(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string H(string value) => WebUtility.HtmlEncode(value);

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
        return name.Length > 0 && name[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'
            ? name
            : "rule_" + name;
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
                ? $"{report.Run.Id}: score {report.Summary.Score} is below {failUnder.Value}"
                : $"{report.Run.Id}: score is unavailable for a {report.Run.Completeness} run");
        if (failOnSeverity is not null)
        {
            var threshold = SeverityRank[failOnSeverity];
            var blocking = report.Observations.SelectMany(observation => observation.Findings).Where(finding =>
                finding.State is "open" or "accepted" &&
                SeverityRank.TryGetValue(finding.Severity, out var rank) && rank <= threshold).ToArray();
            if (blocking.Length > 0)
                failures.Add($"{report.Run.Id}: {blocking.Length} active finding(s) at {failOnSeverity} or higher");
        }
        return failures;
    }
}
