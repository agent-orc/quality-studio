using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class QualityRunReportFactory
{
    public const string AggregateOperationId = "@aggregate";
    private static readonly string[] SeverityNames = ["critical", "high", "medium", "low", "info"];
    private static readonly string[] StateNames = ["open", "accepted", "waived", "false-positive", "resolved"];

    public static QualityRunReportDocument Build(
        ReviewRunManifest manifest,
        ReviewRunStatus status,
        IReadOnlyList<ReviewRunFileTransition> progress,
        IReadOnlyDictionary<string, ReviewObservationSnapshot> snapshots,
        string repositoryRoot,
        string repositoryName,
        int revision,
        IReadOnlyList<QualityRunReportDocument> priorReports)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(snapshots);
        if (!ReviewRunStore.IsTerminal(status.State))
            throw new InvalidOperationException("A canonical quality run report can only be built for a terminal run.");

        var latestProgress = progress.GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var observations = new List<QualityRunObservation>(manifest.Targets.Count + 1);
        foreach (var target in manifest.Targets)
        {
            var outcome = latestProgress.TryGetValue(target.Path, out var transition)
                ? TerminalOutcome(transition.State, status.State)
                : TerminalOutcome("queued", status.State);
            snapshots.TryGetValue(target.Path, out var snapshot);
            observations.Add(ProjectObservation(target.Id, "file", target.Path, outcome, snapshot));
        }
        if (!string.Equals(manifest.Level, "file", StringComparison.Ordinal))
        {
            var outcome = TerminalOutcome(status.AggregateState ?? "queued", status.State);
            snapshots.TryGetValue(AggregateOperationId, out var snapshot);
            observations.Add(ProjectObservation(manifest.Node.Id, manifest.Level, manifest.Node.Path, outcome, snapshot));
        }

        var complete = status.State == "done" && observations.All(observation =>
            observation.Outcome is "done" or "skipped-fresh" && observation.SidecarSha256 is not null);
        var completeness = complete ? "complete" : "partial";
        var targets = manifest.Targets.Select(target => new QualityRunSubjectTarget(
            target.Id, target.Name, NormalizePath(target.Path), target.SubjectHash)).ToArray();
        var execution = BuildExecution(manifest, status, observations, repositoryRoot);
        var summary = BuildSummary(status, observations, complete);
        var identity = new QualityRunIdentity(
            manifest.RunId,
            revision,
            manifest.RepositoryId,
            repositoryName,
            manifest.Kind,
            manifest.Node.Id,
            manifest.Level,
            NormalizePath(manifest.Node.Path),
            status.State,
            completeness,
            manifest.CreatedAt.ToUniversalTime(),
            status.StartedAt?.ToUniversalTime(),
            status.FinishedAt?.ToUniversalTime(),
            manifest.Model ?? "runner-default",
            manifest.ThinkingLevel ?? "model-default",
            manifest.CliType,
            manifest.Force,
            manifest.RepositorySha);
        var delta = BuildDelta(identity, observations, complete, priorReports);
        return new QualityRunReportDocument(
            QualityRunReportJson.SchemaId,
            1,
            identity,
            new QualityRunSubject(QualityRunReportJson.SubjectManifestHash(targets), targets),
            execution,
            observations,
            delta,
            summary);
    }

    private static QualityRunExecution BuildExecution(
        ReviewRunManifest manifest,
        ReviewRunStatus status,
        IReadOnlyList<QualityRunObservation> observations,
        string repositoryRoot)
    {
        var deviation = status.State == "done" && manifest.Estimate is not null &&
                        status.Usage.InputTokens.HasValue && status.Usage.OutputTokens.HasValue
            ? new
            {
                Input = Percent(status.Usage.InputTokens.Value, manifest.Estimate.InputTokens),
                Output = Percent(status.Usage.OutputTokens.Value, manifest.Estimate.OutputTokens),
                Cost = status.CostSpent.HasValue && manifest.Estimate.Cost.HasValue
                    ? Percent(status.CostSpent.Value, manifest.Estimate.Cost.Value)
                    : (decimal?)null,
            }
            : null;
        var capConfigured = status.TokenCap.HasValue || status.CostCap.HasValue;
        return new QualityRunExecution(
            observations.Count(observation => observation.Outcome == "done"),
            observations.Count(observation => observation.Outcome == "skipped-fresh"),
            observations.Count(observation => observation.Outcome == "failed"),
            observations.Count(observation => observation.Outcome == "skipped"),
            observations.Count(observation => observation.Outcome == "cancelled"),
            manifest.Level == "file" ? null : TerminalOutcome(status.AggregateState ?? "queued", status.State),
            status.Errors.Select(error => SanitizeError(error, repositoryRoot)).ToArray(),
            new QualityRunUsage(
                status.UsageOperations,
                status.Usage.InputTokens,
                status.Usage.OutputTokens,
                status.Usage.CachedInputTokens,
                status.Usage.ReasoningOutputTokens,
                Math.Max(0, status.Usage.DurationMs),
                status.CostSpent,
                status.Currency,
                status.PriceStatus,
                deviation?.Input,
                deviation?.Output,
                deviation?.Cost),
            new QualityRunCap(
                status.TokenCap,
                status.CostCap,
                status.State == "capped" ? "reached" : capConfigured ? "within-cap" : "not-configured",
                status.StopReason),
            manifest.Estimate is null
                ? null
                : new QualityRunEstimateEvidence(
                    manifest.Estimate.Files,
                    manifest.Estimate.Operations,
                    manifest.Estimate.InputTokens,
                    manifest.Estimate.OutputTokens,
                    manifest.Estimate.Cost,
                    manifest.Estimate.Currency,
                    manifest.Estimate.HistorySamples,
                    manifest.Estimate.Method));
    }

    private static QualityRunSummary BuildSummary(
        ReviewRunStatus status,
        IReadOnlyList<QualityRunObservation> observations,
        bool complete)
    {
        var current = observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal).ToArray();
        var active = current.Where(finding => finding.State is "open" or "accepted").ToArray();
        var bySeverity = SeverityNames.ToDictionary(name => name,
            name => active.Count(finding => finding.Severity == name), StringComparer.Ordinal);
        var byState = StateNames.ToDictionary(name => name,
            name => current.Count(finding => finding.State == name), StringComparer.Ordinal);
        var grades = observations.Select(observation => observation.Grade?.Score).OfType<int>().ToArray();
        var score = complete && grades.Length == observations.Count
            ? (int?)Math.Round(grades.Average(), MidpointRounding.AwayFromZero)
            : null;
        var highest = SeverityNames.FirstOrDefault(severity => bySeverity[severity] > 0);
        return new QualityRunSummary(
            score,
            score.HasValue ? QualityReportBuilder.Grade(score.Value) : null,
            new QualityRunFindingCounts(active.Length, bySeverity, byState),
            highest,
            complete ? null : PartialReason(status, observations));
    }

    private static QualityRunDelta BuildDelta(
        QualityRunIdentity run,
        IReadOnlyList<QualityRunObservation> observations,
        bool complete,
        IReadOnlyList<QualityRunReportDocument> priorReports)
    {
        if (!complete)
            return UnavailableDelta("Partial runs are not comparable.");
        var prior = priorReports.Where(report =>
                !string.Equals(report.Run.Id, run.Id, StringComparison.Ordinal) &&
                report.Run.State == "done" && report.Run.Completeness == "complete" &&
                string.Equals(report.Run.RepositoryId, run.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(report.Run.Kind, run.Kind, StringComparison.Ordinal) &&
                string.Equals(report.Run.ScopeUnitId, run.ScopeUnitId, StringComparison.Ordinal) &&
                string.Equals(report.Run.Level, run.Level, StringComparison.Ordinal) &&
                report.Run.FinishedAt.HasValue &&
                (!run.FinishedAt.HasValue || report.Run.FinishedAt.Value <= run.FinishedAt.Value))
            .OrderByDescending(report => report.Run.FinishedAt)
            .ThenByDescending(report => report.Run.Revision)
            .FirstOrDefault();
        if (prior is null) return UnavailableDelta("No prior comparable run snapshot exists.");

        var current = ActiveFindingStates(observations);
        var baseline = ActiveFindingStates(prior.Observations);
        var currentKeys = current.Keys.ToHashSet(StringComparer.Ordinal);
        var baselineKeys = baseline.Keys.ToHashSet(StringComparer.Ordinal);
        return new QualityRunDelta(
            "available",
            prior.Run.Id,
            null,
            currentKeys.Except(baselineKeys).Order(StringComparer.Ordinal).ToArray(),
            currentKeys.Intersect(baselineKeys).Order(StringComparer.Ordinal).ToArray(),
            baselineKeys.Except(currentKeys).Order(StringComparer.Ordinal).ToArray(),
            currentKeys.Intersect(baselineKeys).Where(key => current[key] != baseline[key])
                .Order(StringComparer.Ordinal).ToArray());
    }

    private static Dictionary<string, string> ActiveFindingStates(IEnumerable<QualityRunObservation> observations) =>
        observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().State, StringComparer.Ordinal);

    private static QualityRunDelta UnavailableDelta(string reason) =>
        new("unavailable", null, reason, [], [], [], []);

    private static QualityRunObservation ProjectObservation(
        string unitId,
        string level,
        string path,
        string outcome,
        ReviewObservationSnapshot? snapshot)
    {
        if (snapshot is null)
            return new QualityRunObservation(unitId, level, NormalizePath(path), outcome, outcome == "done",
                null, null, null, null, null, null, null, []);
        JsonObject metadata;
        try
        {
            metadata = JsonNode.Parse(snapshot.ReviewMetaJson)?.AsObject()
                       ?? throw new JsonException("Review metadata must be an object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Captured review metadata for '{path}' is invalid.", exception);
        }
        var findings = new List<QualityRunFinding>();
        foreach (var finding in metadata["findings"]?.AsArray().OfType<JsonObject>() ?? [])
            if (ParseFinding(finding, snapshot.FindingStates, "agent", null, null) is { } parsed)
                findings.Add(parsed);
        foreach (var evidence in metadata["deterministicEvidence"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var sensorId = StringAt(evidence, "provenance", "sensorId");
            foreach (var finding in evidence["findings"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var producer = StringAt(finding, "source", "producer");
                if (ParseFinding(finding, snapshot.FindingStates, "deterministic", sensorId, producer) is { } parsed)
                    findings.Add(parsed);
            }
        }
        var score = IntAt(metadata, "grade", "score");
        var grade = score.HasValue
            ? new QualityRunGrade(
                Math.Clamp(score.Value, 0, 100),
                StringAt(metadata, "grade", "band") ?? QualityReportBuilder.Grade(score.Value),
                StringAt(metadata, "grade", "rationale") ?? "No rationale captured.")
            : null;
        return new QualityRunObservation(
            unitId,
            level,
            NormalizePath(path),
            outcome,
            outcome == "done",
            NormalizePath(snapshot.SidecarPath),
            snapshot.SidecarSha256,
            snapshot.CapturedAt.ToUniversalTime(),
            StringAt(metadata, "reviewedHash", "value"),
            StringAt(metadata, "reviewer", "runId"),
            grade,
            metadata["summary"]?.GetValue<string>(),
            findings);
    }

    private static QualityRunFinding? ParseFinding(
        JsonObject finding,
        IReadOnlyDictionary<string, string> states,
        string source,
        string? sensorId,
        string? producer)
    {
        var id = finding["id"]?.GetValue<string>();
        var fingerprint = finding["fingerprint"]?.GetValue<string>();
        if (id is null || fingerprint is null) return null;
        states.TryGetValue(fingerprint, out var state);
        var locations = (finding["locations"]?.AsArray().OfType<JsonObject>() ?? []).Select(location =>
        {
            var range = location["range"]?.AsObject();
            return new QualityFindingLocation(
                NormalizePath(location["path"]?.GetValue<string>() ?? "."),
                IntAt(range, "start", "line"),
                IntAt(range, "start", "column"),
                IntAt(range, "end", "line"),
                IntAt(range, "end", "column"));
        }).ToArray();
        return new QualityRunFinding(
            id,
            finding["ruleId"]?.GetValue<string>() ?? id,
            finding["aspect"]?.GetValue<string>() ?? "general",
            finding["severity"]?.GetValue<string>()?.ToLowerInvariant() ?? "info",
            state ?? "open",
            finding["title"]?.GetValue<string>() ?? id,
            finding["description"]?.GetValue<string>() ?? "No description captured.",
            finding["recommendation"]?.GetValue<string>() ?? "No recommendation captured.",
            finding["evidence"]?.GetValue<string>(),
            fingerprint,
            locations,
            source,
            sensorId,
            producer);
    }

    private static string TerminalOutcome(string state, string runState) => state switch
    {
        "done" or "failed" or "cancelled" or "skipped" or "skipped-fresh" => state,
        _ when runState == "cancelled" => "cancelled",
        _ when runState == "failed" => "failed",
        _ => "skipped",
    };

    private static string PartialReason(ReviewRunStatus status, IReadOnlyList<QualityRunObservation> observations)
    {
        if (status.State != "done") return status.StopReason ?? $"Run ended in state {status.State}.";
        var failed = observations.Count(observation => observation.Outcome == "failed");
        if (failed > 0) return $"{failed} unit(s) failed.";
        var missing = observations.Count(observation => observation.Outcome is "done" or "skipped-fresh" &&
                                                        observation.SidecarSha256 is null);
        if (missing > 0) return $"{missing} completed unit(s) have no captured observation.";
        return "The run did not complete its full scope.";
    }

    private static string SanitizeError(string error, string repositoryRoot)
    {
        var sanitized = error.Replace('\\', '/').Replace(Path.GetFullPath(repositoryRoot).Replace('\\', '/'),
            "[repository-root]", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length <= 4000 ? sanitized : sanitized[..4000];
    }

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length == 0) path = ".";
        if (path.StartsWith("/", StringComparison.Ordinal) ||
            path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' ||
            path.Split('/').Any(segment => segment == ".."))
            throw new InvalidDataException($"Run report path must be repository-relative: {value}");
        return path;
    }

    private static int? IntAt(JsonObject? parent, string objectName, string propertyName) =>
        parent?[objectName]?[propertyName] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static string? StringAt(JsonObject? parent, string objectName, string propertyName) =>
        parent?[objectName]?[propertyName]?.GetValue<string>();

    private static decimal Percent(decimal actual, decimal estimate) =>
        estimate == 0 ? 0 : Math.Round((actual - estimate) / estimate * 100m, 2);
}
