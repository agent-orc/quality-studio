namespace AgentOrchestrator.CodeQuality;

public sealed class QualityRunComparisonException(string message) : ArgumentException(message);

public sealed record QualityRunComparison(
    QualityRunComparisonRun Baseline,
    QualityRunComparisonRun Candidate,
    bool SubjectChanged,
    bool? ReviewInputsChanged,
    bool RouteChanged,
    string Interpretation,
    QualityRunComparisonCounts Counts,
    IReadOnlyList<QualityRunComparisonFinding> Findings);

public sealed record QualityRunComparisonRun(
    string RunId,
    int Revision,
    DateTimeOffset FinishedAt,
    int Score,
    string Grade,
    int ActiveFindings,
    IReadOnlyDictionary<string, int> ActiveBySeverity,
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    string CliType,
    string Model,
    string ThinkingLevel,
    string SubjectManifestHash,
    string? ReviewInputsHash,
    long DurationMs,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost,
    string? Currency);

public sealed record QualityRunComparisonCounts(
    int New,
    int Unchanged,
    int Resolved,
    int DispositionChanged);

public sealed record QualityRunComparisonFinding(
    string Fingerprint,
    string Change,
    string Title,
    string Severity,
    string? BaselineState,
    string? CandidateState,
    IReadOnlyList<QualityFindingLocation> Locations);

public static class QualityRunComparisonBuilder
{
    private static readonly string[] SeverityNames = ["critical", "high", "medium", "low", "info"];

    public static QualityRunComparison Build(
        QualityRunReportDocument baseline,
        QualityRunReportDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateComparable(baseline, "baseline");
        ValidateComparable(candidate, "candidate");
        if (string.Equals(baseline.Run.Id, candidate.Run.Id, StringComparison.Ordinal))
            throw new QualityRunComparisonException("Baseline and candidate must be different review runs.");
        if (!string.Equals(baseline.Run.RepositoryId, candidate.Run.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Run.Kind, candidate.Run.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Run.ScopeUnitId, candidate.Run.ScopeUnitId, StringComparison.Ordinal) ||
            !string.Equals(baseline.Run.Level, candidate.Run.Level, StringComparison.OrdinalIgnoreCase))
            throw new QualityRunComparisonException("Review runs are not compatible. Repository, kind, scope, and level must match.");
        if (candidate.Run.FinishedAt!.Value <= baseline.Run.FinishedAt!.Value)
            throw new QualityRunComparisonException("The candidate must finish after the baseline.");

        var baselineFindings = FindingsByFingerprint(baseline);
        var candidateFindings = FindingsByFingerprint(candidate);
        var findings = baselineFindings.Keys.Union(candidateFindings.Keys, StringComparer.Ordinal)
            .Select(fingerprint => CompareFinding(
                fingerprint,
                baselineFindings.GetValueOrDefault(fingerprint),
                candidateFindings.GetValueOrDefault(fingerprint)))
            .OrderBy(finding => ChangeRank(finding.Change))
            .ThenBy(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Title, StringComparer.Ordinal)
            .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var subjectChanged = !string.Equals(
            baseline.Subject.ManifestHash, candidate.Subject.ManifestHash, StringComparison.Ordinal);
        var baselineInputs = ReviewInputsHash(baseline);
        var candidateInputs = ReviewInputsHash(candidate);
        var reviewInputsChanged = baselineInputs is null || candidateInputs is null
            ? (bool?)null
            : !string.Equals(baselineInputs, candidateInputs, StringComparison.Ordinal);
        var routeChanged = !string.Equals(baseline.Run.CliType, candidate.Run.CliType, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(baseline.Run.Model, candidate.Run.Model, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(baseline.Run.ThinkingLevel, candidate.Run.ThinkingLevel, StringComparison.OrdinalIgnoreCase);
        return new QualityRunComparison(
            ProjectRun(baseline, baselineInputs),
            ProjectRun(candidate, candidateInputs),
            subjectChanged,
            reviewInputsChanged,
            routeChanged,
            Interpretation(subjectChanged, reviewInputsChanged, routeChanged),
            new QualityRunComparisonCounts(
                findings.Count(finding => finding.Change == "new"),
                findings.Count(finding => finding.Change == "unchanged"),
                findings.Count(finding => finding.Change == "resolved"),
                findings.Count(finding => finding.Change == "disposition-changed")),
            findings);
    }

    private static void ValidateComparable(QualityRunReportDocument report, string role)
    {
        if (report.Run.State != "done" || report.Run.Completeness != "complete" ||
            !report.Run.FinishedAt.HasValue || !report.Summary.Score.HasValue || report.Summary.Grade is null)
            throw new QualityRunComparisonException(
                $"The {role} run is not comparable. Only complete, scored runs can be compared.");
    }

    private static Dictionary<string, QualityRunFinding> FindingsByFingerprint(QualityRunReportDocument report) =>
        report.Observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static QualityRunComparisonFinding CompareFinding(
        string fingerprint,
        QualityRunFinding? baseline,
        QualityRunFinding? candidate)
    {
        var change = baseline is null ? "new"
            : candidate is null ? "resolved"
            : baseline.State != candidate.State ? "disposition-changed"
            : "unchanged";
        var evidence = candidate ?? baseline!;
        return new QualityRunComparisonFinding(
            fingerprint,
            change,
            evidence.Title,
            evidence.Severity,
            baseline?.State,
            candidate?.State,
            evidence.Locations);
    }

    private static QualityRunComparisonRun ProjectRun(QualityRunReportDocument report, string? reviewInputsHash)
    {
        var active = FindingsByFingerprint(report).Values
            .Where(finding => finding.State is "open" or "accepted").ToArray();
        return new QualityRunComparisonRun(
            report.Run.Id,
            report.Run.Revision,
            report.Run.FinishedAt!.Value,
            report.Summary.Score!.Value,
            report.Summary.Grade!,
            active.Length,
            SeverityNames.ToDictionary(severity => severity,
                severity => active.Count(finding => finding.Severity == severity), StringComparer.Ordinal),
            report.Execution.Reviewed,
            report.Execution.ReusedFresh,
            report.Execution.Failed,
            report.Execution.Skipped,
            report.Run.CliType,
            report.Run.Model,
            report.Run.ThinkingLevel,
            report.Subject.ManifestHash,
            reviewInputsHash,
            report.Execution.Usage.DurationMs,
            report.Execution.Usage.InputTokens,
            report.Execution.Usage.OutputTokens,
            report.Execution.Usage.Cost,
            report.Execution.Usage.Currency);
    }

    private static string? ReviewInputsHash(QualityRunReportDocument report)
    {
        if (report.Observations.Count == 0 || report.Observations.Any(observation =>
                string.IsNullOrWhiteSpace(observation.ReviewInputsHash))) return null;
        return QualityRunReportJson.ReviewInputsManifestHash(report.Observations.Select(observation =>
            new KeyValuePair<string, string>(observation.UnitId, observation.ReviewInputsHash!)));
    }

    private static string Interpretation(bool subjectChanged, bool? reviewInputsChanged, bool routeChanged)
    {
        if (reviewInputsChanged is null)
            return "Review-input provenance is unavailable for at least one run. Compare outcomes only; do not attribute the delta to the model.";
        if (routeChanged || subjectChanged || reviewInputsChanged.Value)
            return "Route, source subject, or review inputs changed. Compare outcomes only; do not attribute the delta to the model.";
        return "Route, source subject, and review inputs match. The quality delta is still observational, not a causal model result.";
    }

    private static int ChangeRank(string change) => change switch
    {
        "new" => 0,
        "disposition-changed" => 1,
        "resolved" => 2,
        _ => 3,
    };

    private static int SeverityRank(string severity) => Array.IndexOf(SeverityNames, severity) is var index && index >= 0
        ? index
        : SeverityNames.Length;
}
