using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>One side (baseline or candidate) of a run comparison, loaded from an immutable outcome snapshot.</summary>
public sealed record QualityRunComparisonSide(
    string RunId,
    string Status,
    string? Error,
    QualityRunIdentity? Run,
    QualityRunSummary? Summary,
    QualityRunExecution? Execution,
    string? SubjectManifestHash);

public sealed record QualityRunComparisonFinding(
    string Fingerprint,
    string Category,
    string Severity,
    string Title,
    string RuleId,
    string? BaselineState,
    string? CandidateState,
    IReadOnlyList<QualityFindingLocation> Locations);

public sealed record QualityRunComparisonDelta(
    IReadOnlyList<QualityRunComparisonFinding> New,
    IReadOnlyList<QualityRunComparisonFinding> Unchanged,
    IReadOnlyList<QualityRunComparisonFinding> Resolved,
    IReadOnlyList<QualityRunComparisonFinding> DispositionChanged);

/// <summary>
/// Whether a baseline/candidate pair supports a causal quality claim. A finding delta is still returned
/// when these flags are false; the caller must not attribute it to the model alone.
/// </summary>
public sealed record QualityRunComparisonCompatibility(
    bool SameScope,
    bool RouteMatches,
    bool InputsMatch,
    IReadOnlyList<string> Reasons);

public sealed record QualityRunComparisonResponse(
    QualityRunComparisonSide Baseline,
    QualityRunComparisonSide Candidate,
    bool Comparable,
    QualityRunComparisonCompatibility? Compatibility,
    QualityRunComparisonDelta? Delta);

/// <summary>
/// Compares two immutable <see cref="QualityRunReportDocument"/> outcome snapshots by fingerprint.
/// Findings absent from the candidate outcome are treated as resolved, never as merely filtered out.
/// </summary>
public static class QualityRunComparisonBuilder
{
    public static QualityRunComparisonResponse Build(
        string repositoryId, string baselineId, string candidateId, QualityRunReportStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
        ArgumentNullException.ThrowIfNull(store);

        var (baselineStatus, baselineReport, baselineError) = Attempt(repositoryId, baselineId, store);
        var (candidateStatus, candidateReport, candidateError) = Attempt(repositoryId, candidateId, store);
        var baselineSide = Side(baselineId, baselineStatus, baselineError, baselineReport);
        var candidateSide = Side(candidateId, candidateStatus, candidateError, candidateReport);
        if (baselineReport is null || candidateReport is null)
            return new QualityRunComparisonResponse(baselineSide, candidateSide, false, null, null);

        return new QualityRunComparisonResponse(
            baselineSide, candidateSide, true,
            BuildCompatibility(baselineReport, candidateReport),
            BuildDelta(baselineReport, candidateReport));
    }

    private static (string Status, QualityRunReportDocument? Report, string? Error) Attempt(
        string repositoryId, string runId, QualityRunReportStore store)
    {
        try
        {
            var report = store.Load(runId);
            // A mismatched repository owner is reported identically to a missing snapshot so a run id
            // guess from another repository can never disclose that the run exists.
            if (!string.Equals(report.Run.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                return ("missing", null, $"No outcome snapshot exists for run '{runId}'.");
            return ("ok", report, null);
        }
        catch (FileNotFoundException)
        {
            return ("missing", null, $"No outcome snapshot exists for run '{runId}'.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return ("corrupt", null, $"The stored outcome snapshot for run '{runId}' could not be read.");
        }
    }

    private static QualityRunComparisonSide Side(
        string runId, string status, string? error, QualityRunReportDocument? report) =>
        new(runId, status, error, report?.Run, report?.Summary, report?.Execution, report?.Subject.ManifestHash);

    private static QualityRunComparisonCompatibility BuildCompatibility(
        QualityRunReportDocument baseline, QualityRunReportDocument candidate)
    {
        var sameScope = string.Equals(baseline.Run.RepositoryId, candidate.Run.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(baseline.Run.Kind, candidate.Run.Kind, StringComparison.Ordinal) &&
                        string.Equals(baseline.Run.ScopeUnitId, candidate.Run.ScopeUnitId, StringComparison.Ordinal) &&
                        string.Equals(baseline.Run.Level, candidate.Run.Level, StringComparison.Ordinal);
        var routeMatches = string.Equals(baseline.Run.Model, candidate.Run.Model, StringComparison.Ordinal) &&
                           string.Equals(baseline.Run.ThinkingLevel, candidate.Run.ThinkingLevel, StringComparison.Ordinal) &&
                           string.Equals(baseline.Run.CliType, candidate.Run.CliType, StringComparison.Ordinal);
        var inputsMatch = string.Equals(baseline.Subject.ManifestHash, candidate.Subject.ManifestHash, StringComparison.Ordinal);
        var reasons = new List<string>();
        if (!sameScope) reasons.Add("Baseline and candidate cover a different repository, kind, scope, or level.");
        if (!routeMatches) reasons.Add("Route changed.");
        if (!inputsMatch) reasons.Add("Inputs changed.");
        return new QualityRunComparisonCompatibility(sameScope, routeMatches, inputsMatch, reasons);
    }

    private static QualityRunComparisonDelta BuildDelta(
        QualityRunReportDocument baseline, QualityRunReportDocument candidate)
    {
        var baselineFindings = ActiveFindings(baseline.Observations);
        var candidateFindings = ActiveFindings(candidate.Observations);
        var baselineKeys = baselineFindings.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateKeys = candidateFindings.Keys.ToHashSet(StringComparer.Ordinal);
        var common = baselineKeys.Intersect(candidateKeys).Order(StringComparer.Ordinal).ToArray();

        var added = candidateKeys.Except(baselineKeys).Order(StringComparer.Ordinal)
            .Select(key => Project(candidateFindings[key], "new", null, candidateFindings[key].State)).ToArray();
        var resolved = baselineKeys.Except(candidateKeys).Order(StringComparer.Ordinal)
            .Select(key => Project(baselineFindings[key], "resolved", baselineFindings[key].State, null)).ToArray();
        var unchanged = common.Where(key => baselineFindings[key].State == candidateFindings[key].State)
            .Select(key => Project(candidateFindings[key], "unchanged", baselineFindings[key].State, candidateFindings[key].State)).ToArray();
        var changed = common.Where(key => baselineFindings[key].State != candidateFindings[key].State)
            .Select(key => Project(candidateFindings[key], "dispositionChanged", baselineFindings[key].State, candidateFindings[key].State)).ToArray();

        return new QualityRunComparisonDelta(added, unchanged, resolved, changed);
    }

    private static Dictionary<string, QualityRunFinding> ActiveFindings(IEnumerable<QualityRunObservation> observations) =>
        observations.SelectMany(observation => observation.Findings)
            .Where(finding => finding.State != "resolved")
            .GroupBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static QualityRunComparisonFinding Project(
        QualityRunFinding finding, string category, string? baselineState, string? candidateState) =>
        new(finding.Fingerprint, category, finding.Severity, finding.Title, finding.RuleId,
            baselineState, candidateState, finding.Locations);
}
