namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRunComparison(
    QualityRunComparisonSnapshot Baseline,
    QualityRunComparisonSnapshot Candidate,
    QualityRunComparisonProvenance Provenance,
    IReadOnlyDictionary<string, int> FindingCounts,
    IReadOnlyList<QualityRunFindingDelta> Findings);

public sealed record QualityRunComparisonSnapshot(
    string RunId,
    int Revision,
    DateTimeOffset FinishedAt,
    string Model,
    string ThinkingLevel,
    string CliType,
    bool Force,
    string SubjectManifestHash,
    int? Score,
    string? Grade,
    int ActiveFindings,
    IReadOnlyDictionary<string, int> FindingsBySeverity,
    int Reviewed,
    int ReusedFresh,
    int Failed,
    int Skipped,
    long? InputTokens,
    long? OutputTokens,
    long DurationMs,
    decimal? Cost,
    string? Currency);

public sealed record QualityRunComparisonProvenance(
    bool RouteChanged,
    bool SubjectChanged,
    bool ForceChanged,
    string Interpretation,
    string EvidenceLimit);

public sealed record QualityRunFindingDelta(
    string Category,
    string Fingerprint,
    string? BaselineState,
    string? CandidateState,
    QualityRunFinding Finding);

/// <summary>Builds an outcome-only comparison from two immutable canonical run reports.</summary>
public static class QualityRunComparisonBuilder
{
    private static readonly string[] Categories = ["new", "dispositionChanged", "resolved", "unchanged"];
    private static readonly string[] Severities = ["critical", "high", "medium", "low", "info"];

    public static QualityRunComparison Build(
        QualityRunReportDocument baseline,
        QualityRunReportDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateCompatibility(baseline, candidate);

        var baselineFindings = FindingSet(baseline);
        var candidateFindings = FindingSet(candidate);
        var deltas = new List<QualityRunFindingDelta>(
            baselineFindings.Count + candidateFindings.Count);

        foreach (var fingerprint in candidateFindings.Keys.Except(baselineFindings.Keys, StringComparer.Ordinal))
        {
            var finding = candidateFindings[fingerprint];
            deltas.Add(new QualityRunFindingDelta("new", fingerprint, null, finding.State, finding));
        }
        foreach (var fingerprint in baselineFindings.Keys.Except(candidateFindings.Keys, StringComparer.Ordinal))
        {
            var finding = baselineFindings[fingerprint];
            deltas.Add(new QualityRunFindingDelta("resolved", fingerprint, finding.State, null, finding));
        }
        foreach (var fingerprint in candidateFindings.Keys.Intersect(baselineFindings.Keys, StringComparer.Ordinal))
        {
            var before = baselineFindings[fingerprint];
            var after = candidateFindings[fingerprint];
            var category = string.Equals(before.State, after.State, StringComparison.Ordinal)
                ? "unchanged"
                : "dispositionChanged";
            deltas.Add(new QualityRunFindingDelta(category, fingerprint, before.State, after.State, after));
        }

        var ordered = deltas
            .OrderBy(delta => Array.IndexOf(Categories, delta.Category))
            .ThenBy(delta => Array.IndexOf(Severities, delta.Finding.Severity))
            .ThenBy(delta => delta.Finding.Locations.FirstOrDefault()?.Path ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(delta => delta.Finding.Title, StringComparer.Ordinal)
            .ThenBy(delta => delta.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var routeChanged = !string.Equals(baseline.Run.Model, candidate.Run.Model, StringComparison.Ordinal) ||
                           !string.Equals(baseline.Run.ThinkingLevel, candidate.Run.ThinkingLevel, StringComparison.Ordinal) ||
                           !string.Equals(baseline.Run.CliType, candidate.Run.CliType, StringComparison.Ordinal);
        var subjectChanged = !string.Equals(
            baseline.Subject.ManifestHash, candidate.Subject.ManifestHash, StringComparison.Ordinal);
        var forceChanged = baseline.Run.Force != candidate.Run.Force;
        var changed = routeChanged || subjectChanged || forceChanged;
        var interpretation = changed
            ? "Route, reviewed subject, or force mode changed. Compare the recorded outcomes, but do not attribute the delta to the model."
            : "Route, reviewed subject, and force mode match. This records an outcome delta; it does not establish why the outcome changed.";

        return new QualityRunComparison(
            Snapshot(baseline),
            Snapshot(candidate),
            new QualityRunComparisonProvenance(
                routeChanged,
                subjectChanged,
                forceChanged,
                interpretation,
                "Canonical report v1 records reviewed subject hashes, but not a complete prompt-input hash."),
            Categories.ToDictionary(category => category,
                category => ordered.Count(delta => delta.Category == category), StringComparer.Ordinal),
            ordered);
    }

    private static void ValidateCompatibility(
        QualityRunReportDocument baseline,
        QualityRunReportDocument candidate)
    {
        if (string.Equals(baseline.Run.Id, candidate.Run.Id, StringComparison.Ordinal))
            throw new ArgumentException("Choose two different review runs.");
        if (baseline.Run.State != "done" || baseline.Run.Completeness != "complete" ||
            candidate.Run.State != "done" || candidate.Run.Completeness != "complete" ||
            !baseline.Run.FinishedAt.HasValue || !candidate.Run.FinishedAt.HasValue)
            throw new ArgumentException("Only complete, successful run snapshots can be compared.");
        if (!string.Equals(baseline.Run.RepositoryId, candidate.Run.RepositoryId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Run.Kind, candidate.Run.Kind, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseline.Run.ScopeUnitId, candidate.Run.ScopeUnitId, StringComparison.Ordinal) ||
            !string.Equals(baseline.Run.Level, candidate.Run.Level, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Runs must have the same repository, review kind, scope, and level.");
        if (baseline.Run.FinishedAt.Value > candidate.Run.FinishedAt.Value)
            throw new ArgumentException("The baseline must finish before the candidate.");
    }

    private static Dictionary<string, QualityRunFinding> FindingSet(QualityRunReportDocument report) =>
        report.Observations
            .SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static QualityRunComparisonSnapshot Snapshot(QualityRunReportDocument report)
    {
        var active = FindingSet(report).Values
            .Where(finding => finding.State is "open" or "accepted")
            .ToArray();
        return new QualityRunComparisonSnapshot(
            report.Run.Id,
            report.Run.Revision,
            report.Run.FinishedAt!.Value,
            report.Run.Model,
            report.Run.ThinkingLevel,
            report.Run.CliType,
            report.Run.Force,
            report.Subject.ManifestHash,
            report.Summary.Score,
            report.Summary.Grade,
            active.Length,
            Severities.ToDictionary(severity => severity,
                severity => active.Count(finding => finding.Severity == severity), StringComparer.Ordinal),
            report.Execution.Reviewed,
            report.Execution.ReusedFresh,
            report.Execution.Failed,
            report.Execution.Skipped,
            report.Execution.Usage.InputTokens,
            report.Execution.Usage.OutputTokens,
            report.Execution.Usage.DurationMs,
            report.Execution.Usage.Cost,
            report.Execution.Usage.Currency);
    }
}
