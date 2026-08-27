namespace AgentOrchestrator.CodeQuality;

public sealed record QualityRunComparisonFinding(
    string Fingerprint,
    string Severity,
    string Title,
    string RuleId,
    string? BaselineState,
    string? CandidateState,
    IReadOnlyList<QualityFindingLocation> Locations);

/// <summary>
/// Whether a quality delta between two runs can be attributed to code change alone. When the route or
/// reviewed inputs differ, a score movement may instead reflect the route change, not the code.
/// </summary>
public sealed record QualityRunComparisonRoute(bool Compatible, IReadOnlyList<string> Differences);

public sealed record QualityRunComparison(
    string BaselineRunId,
    string CandidateRunId,
    QualityRunComparisonRoute Route,
    IReadOnlyList<QualityRunComparisonFinding> New,
    IReadOnlyList<QualityRunComparisonFinding> Unchanged,
    IReadOnlyList<QualityRunComparisonFinding> Resolved,
    IReadOnlyList<QualityRunComparisonFinding> DispositionChanged);

/// <summary>Aligns two immutable run-outcome snapshots by finding fingerprint.</summary>
public static class QualityRunComparer
{
    public static QualityRunComparison Compare(QualityRunReportDocument baseline, QualityRunReportDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var baselineFindings = ActiveFindingsByFingerprint(baseline.Observations);
        var candidateFindings = ActiveFindingsByFingerprint(candidate.Observations);
        var baselineKeys = baselineFindings.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateKeys = candidateFindings.Keys.ToHashSet(StringComparer.Ordinal);
        var shared = candidateKeys.Intersect(baselineKeys).Order(StringComparer.Ordinal).ToArray();

        var added = candidateKeys.Except(baselineKeys).Order(StringComparer.Ordinal)
            .Select(fingerprint => Project(candidateFindings[fingerprint], null, candidateFindings[fingerprint].State))
            .ToArray();
        var resolved = baselineKeys.Except(candidateKeys).Order(StringComparer.Ordinal)
            .Select(fingerprint => Project(baselineFindings[fingerprint], baselineFindings[fingerprint].State, null))
            .ToArray();
        var unchanged = shared.Where(fingerprint => baselineFindings[fingerprint].State == candidateFindings[fingerprint].State)
            .Select(fingerprint => Project(candidateFindings[fingerprint],
                baselineFindings[fingerprint].State, candidateFindings[fingerprint].State))
            .ToArray();
        var dispositionChanged = shared.Where(fingerprint => baselineFindings[fingerprint].State != candidateFindings[fingerprint].State)
            .Select(fingerprint => Project(candidateFindings[fingerprint],
                baselineFindings[fingerprint].State, candidateFindings[fingerprint].State))
            .ToArray();

        return new QualityRunComparison(
            baseline.Run.Id, candidate.Run.Id, DescribeRoute(baseline, candidate), added, unchanged, resolved, dispositionChanged);
    }

    private static QualityRunComparisonFinding Project(
        QualityRunFinding source, string? baselineState, string? candidateState) =>
        new(source.Fingerprint, source.Severity, source.Title, source.RuleId, baselineState, candidateState, source.Locations);

    private static Dictionary<string, QualityRunFinding> ActiveFindingsByFingerprint(
        IEnumerable<QualityRunObservation> observations) =>
        observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static QualityRunComparisonRoute DescribeRoute(QualityRunReportDocument baseline, QualityRunReportDocument candidate)
    {
        var differences = new List<string>();
        if (!string.Equals(baseline.Run.Kind, candidate.Run.Kind, StringComparison.Ordinal))
            differences.Add($"Review kind changed from '{baseline.Run.Kind}' to '{candidate.Run.Kind}'.");
        if (!string.Equals(baseline.Run.Model, candidate.Run.Model, StringComparison.Ordinal))
            differences.Add($"Model changed from '{baseline.Run.Model}' to '{candidate.Run.Model}'.");
        if (!string.Equals(baseline.Run.ThinkingLevel, candidate.Run.ThinkingLevel, StringComparison.Ordinal))
            differences.Add($"Thinking level changed from '{baseline.Run.ThinkingLevel}' to '{candidate.Run.ThinkingLevel}'.");
        if (!string.Equals(baseline.Run.CliType, candidate.Run.CliType, StringComparison.Ordinal))
            differences.Add($"CLI changed from '{baseline.Run.CliType}' to '{candidate.Run.CliType}'.");
        if (!string.Equals(baseline.Subject.ManifestHash, candidate.Subject.ManifestHash, StringComparison.Ordinal))
            differences.Add("The reviewed inputs (subject manifest) differ between the two runs.");
        return new QualityRunComparisonRoute(differences.Count == 0, differences);
    }
}
