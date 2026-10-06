using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record TreeResponse(
    string Path,
    IReadOnlyList<TreeNodeResponse> Nodes,
    GitStateResponse? GitState = null);

/// <summary>
/// Present only when the hierarchy could not follow the working tree. Absent means Git answered and the
/// snapshot is current; a value means the nodes below reflect the last state Git could report.
/// </summary>
public sealed record GitStateResponse(string Status, string? Detail);

public sealed record ScopeExclusionResponse(string Path, string Reason);

public sealed record TreeNodeResponse(
    string Id,
    string Name,
    string Level,
    string Path,
    IReadOnlyDictionary<string, KindStateResponse> Kinds,
    int FindingsCount,
    FindingStateCounts FindingCounts,
    string? ReviewedAt,
    long? SizeBytes,
    int? LineCount,
    CoverageAggregate Coverage,
    IReadOnlyList<ScopeExclusionResponse> Excluded,
    IReadOnlyList<TreeNodeResponse> Children)
{
    public static TreeNodeResponse From(
        HierarchyNode node,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        CoverageSnapshot? coverage = null,
        string? currentCommit = null,
        IReadOnlyDictionary<string, FindingSuppressionRule>? suppressions = null)
    {
        var reviewSummary = DirectReviewSummary.FromTree(node, states);
        var descendantFiles = Flatten(node).Where(candidate => candidate.Level == ReviewLevel.File)
            .Select(candidate => candidate.Path).Distinct(StringComparer.Ordinal).ToArray();
        return new(
            node.Id,
            node.Name,
            node.Level.ToString().ToLowerInvariant(),
            node.Path,
            node.AggregatedStates.ToDictionary(
                pair => pair.Key.ToString().ToLowerInvariant(),
                pair => KindStateResponse.From(node, pair.Value, states, suppressions),
                StringComparer.Ordinal),
            reviewSummary.FindingsCount,
            reviewSummary.Counts,
            reviewSummary.ReviewedAt,
            node.SizeBytes,
            node.LineCount,
            CoverageProjection.ForPath(coverage, currentCommit, node.Path, node.Level == ReviewLevel.File, descendantFiles),
            node.Exclusions.OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Reason, StringComparer.Ordinal)
                .Select(item => new ScopeExclusionResponse(item.Path, item.Reason)).ToArray(),
            node.Children.Select(child => From(child, states, coverage, currentCommit)).ToArray());
    }

    private static IEnumerable<HierarchyNode> Flatten(HierarchyNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }

    private sealed record DirectReviewSummary(int FindingsCount, FindingStateCounts Counts, string? ReviewedAt)
    {
        public static DirectReviewSummary FromTree(HierarchyNode node, IReadOnlyDictionary<string, FindingStateRecord> states)
        {
            var direct = From(node, states);
            var counts = direct.Counts;
            var findingsCount = direct.FindingsCount;
            DateTimeOffset? reviewedAt = direct.ReviewedAt is null ? null : DateTimeOffset.Parse(direct.ReviewedAt);
            foreach (var child in node.Children)
            {
                var descendant = FromTree(child, states);
                counts += descendant.Counts;
                findingsCount += descendant.FindingsCount;
                if (descendant.ReviewedAt is not null)
                {
                    var candidate = DateTimeOffset.Parse(descendant.ReviewedAt);
                    if (reviewedAt is null || candidate > reviewedAt) reviewedAt = candidate;
                }
            }
            if (node.Level == ReviewLevel.File)
            {
                var visible = counts.Open + counts.Accepted + counts.Waived + counts.FalsePositive;
                var resolvedForPath = states.Values.Count(state => state.State == FindingState.Resolved &&
                    string.Equals(state.Path, node.Path, StringComparison.Ordinal));
                counts = counts with { Resolved = resolvedForPath };
                findingsCount = visible;
            }
            return new(findingsCount, counts, reviewedAt?.ToString("O"));
        }

        private static DirectReviewSummary From(HierarchyNode node, IReadOnlyDictionary<string, FindingStateRecord> states)
        {
            var findingsCount = 0;
            var counts = FindingStateCounts.Empty;
            DateTimeOffset? reviewedAt = null;
            foreach (var document in node.Documents.Values)
            {
                if (document.Payload is null)
                {
                    continue;
                }

                using var json = JsonDocument.Parse(document.Payload);
                var root = json.RootElement;
                if (root.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array)
                {
                    findingsCount += findings.GetArrayLength();
                }
                var metadata = JsonNode.Parse(document.Payload)!.AsObject();
                counts += FindingStateProjection.Count(metadata, states);

                if (root.TryGetProperty("reviewedAt", out var reviewedAtElement) &&
                    reviewedAtElement.TryGetDateTimeOffset(out var candidate) &&
                    (reviewedAt is null || candidate > reviewedAt))
                {
                    reviewedAt = candidate;
                }
            }

            return new(findingsCount, counts, reviewedAt?.ToString("O"));
        }
    }
}

/// <param name="Projection">
/// Present on a directory or other container without a grade of its own: the size-weighted mean of its
/// descendant files' grades. A projection, not a review - see <see cref="GradeProjectionResponse"/>.
/// </param>
public sealed record KindStateResponse(
    string Direct,
    string Descendants,
    string Overall,
    int? Score,
    string? Band,
    string? MetaPath,
    GradeProjectionResponse? Projection = null)
{
    public static KindStateResponse From(
        HierarchyNode node,
        KindAggregation aggregation,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        IReadOnlyDictionary<string, FindingSuppressionRule>? suppressions = null)
    {
        int? score = null;
        string? band = null;
        string? metaPath = null;
        if (node.Documents.TryGetValue(aggregation.Kind, out var document))
        {
            metaPath = document.SourcePath;
            if (document.Payload is not null)
            {
                var metadata = JsonNode.Parse(document.Payload)!.AsObject();
                // The ignore list must reach the grade here too, or the hierarchy badge and the
                // review panel would report different effective grades for the same file.
                var projected = FindingStateProjection.Apply(metadata, states, suppressions);
                if (projected["grade"] is JsonObject grade)
                {
                    score = grade["score"]?.GetValue<int>();
                    band = grade["band"]?.GetValue<string>();
                }
            }
        }

        return new(Map(aggregation.Direct), Map(aggregation.Descendants), Map(aggregation.Overall), score, band, metaPath);
    }

    // Hierarchy aggregation has no `invalid`: a sidecar the contract rejects attaches to no unit,
    // so the unit degrades to "missing" here while /api/scan reports it as `invalid` per subject.
    private static string Map(ReviewState state) => state switch
    {
        ReviewState.Current => "fresh",
        ReviewState.Stale => "stale",
        ReviewState.PolicyDrift => "policy-drift",
        _ => "missing",
    };
}

/// <summary>
/// A directory grade projected from its files until an aggregate review grades the directory itself:
/// each graded descendant file weighs by its line count, so a 2,000-line file moves the projection
/// more than a 20-line one. <see cref="GradedFiles"/> of <see cref="Files"/> says how much of the
/// directory the projection actually rests on; ungraded files neither count as zero nor as passing.
/// </summary>
public sealed record GradeProjectionResponse(
    int Score,
    string Band,
    int GradedFiles,
    int Files,
    long WeightedLines,
    string Basis = GradeProjectionResponse.SizeWeightedFileGrades)
{
    public const string SizeWeightedFileGrades = "size-weighted-file-grades";
}

/// <summary>The running sums behind a <see cref="GradeProjectionResponse"/>, added up the tree.</summary>
public readonly record struct GradeRollup(decimal WeightedScore, long Weight, int Graded, int Files)
{
    /// <summary>Approximate characters per line, for a file whose line count is unknown.</summary>
    private const long BytesPerLine = 40;

    public static GradeRollup ForFile(int? score, int? lineCount, long? sizeBytes)
    {
        var weight = lineCount is > 0 ? lineCount.Value : Math.Max(1, (sizeBytes ?? 0) / BytesPerLine);
        return score is int graded ? new(graded * (decimal)weight, weight, 1, 1) : new(0, 0, 0, 1);
    }

    public static GradeRollup operator +(GradeRollup left, GradeRollup right) => new(
        left.WeightedScore + right.WeightedScore, left.Weight + right.Weight,
        left.Graded + right.Graded, left.Files + right.Files);

    public GradeProjectionResponse? ToProjection()
    {
        if (Graded == 0 || Weight == 0) return null;
        var score = (int)Math.Round(WeightedScore / Weight, MidpointRounding.AwayFromZero);
        return new GradeProjectionResponse(score, BandOf(score), Graded, Files, Weight);
    }

    /// <summary>The band scale review grades use (see <c>FindingStateProjection</c>).</summary>
    public static string BandOf(int score) => score switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F",
    };
}

public sealed record FileResponse(
    string Path,
    string Content,
    IReadOnlyList<JsonElement> MetaDocuments,
    long SizeBytes,
    string LineEnding,
    string Encoding,
    CoverageAggregate Coverage,
    LargeFileResponse? LargeFile = null,
    AnalyzerFileView? Analyzers = null);

/// <summary>Persisted deterministic analyzer findings per repository-relative file path.</summary>
public sealed record AnalyzerCountsResponse(IReadOnlyDictionary<string, int> Files);

/// <summary>
/// Present when the file is larger than <c>QualityStudio:Limits:MaxFileBytes</c>. The response then
/// carries the first <see cref="ReturnedBytes"/> bytes in <c>content</c> instead of the whole file,
/// cut at a character boundary; <c>sizeBytes</c> stays the true size of the file on disk.
/// </summary>
public sealed record LargeFileResponse(long SizeBytes, long LimitBytes, long ReturnedBytes);

public sealed record RiskRowResponse(
    string Path,
    string Name,
    int? GradeScore,
    string? GradeBand,
    string ReviewState,
    CoverageAggregate Coverage,
    int Changes,
    decimal? RiskScore,
    ComplexitySummaryResponse? Complexity = null);

/// <summary>
/// A file's complexity as the risk view shows it: sums and maxima over its functions, the complexity
/// pressure that enters the risk score, and the few functions that drive it.
/// </summary>
public sealed record ComplexitySummaryResponse(
    string Language,
    int Cyclomatic,
    int Cognitive,
    int MaxCyclomatic,
    int MaxCognitive,
    int Functions,
    int Pressure,
    IReadOnlyList<FunctionComplexity> Hotspots)
{
    /// <summary>How many of the most complex functions a risk row carries.</summary>
    public const int HotspotCount = 3;

    public static ComplexitySummaryResponse From(FileComplexity file) => new(
        file.Language, file.Cyclomatic, file.Cognitive, file.MaxCyclomatic, file.MaxCognitive,
        file.Functions.Count, file.Pressure,
        file.Functions.OrderByDescending(function => function.Cognitive)
            .ThenByDescending(function => function.Cyclomatic)
            .ThenBy(function => function.Line)
            .Take(HotspotCount).ToArray());
}

/// <summary>Every function of one file, for <c>GET /api/repos/{repoId}/complexity?path=</c>.</summary>
public sealed record FileComplexityResponse(
    string AnalyzerVersion,
    int CognitiveThreshold,
    FileComplexity File);

public sealed record RiskMatrixCellResponse(
    string Grade,
    string Coverage,
    int Files,
    int Changes);

public sealed record RiskResponse(
    int Days,
    string? CurrentCommit,
    IReadOnlyList<RiskRowResponse> Rows,
    IReadOnlyList<RiskMatrixCellResponse> Matrix);

public sealed record GuidelineTraceFindingResponse(
    string Id,
    string RuleId,
    string Title,
    string Severity,
    string Kind,
    string UnitPath,
    string MetaPath);

public sealed record GuidelineTraceResponse(
    string GuidelineId,
    int FindingsCount,
    IReadOnlyList<GuidelineTraceFindingResponse> Findings);

public sealed record HandoverConfigurationResponse(bool TargetConfigured, bool DryRun, string? Project);

public sealed record SecurityScanResponse(
    string Verdict,
    bool Available,
    string Scanner,
    string Version,
    string Mode,
    string? Range,
    string? ConfigPath,
    string? BaselinePath,
    string ScannedAt,
    int FilesScanned,
    int NewFindings,
    int AcceptedFindings,
    int BlockFindings,
    int WarnFindings,
    int CleanFiles,
    string? UnavailableReason,
    SecurityScanProvenanceResponse Provenance,
    SecurityScanCountsResponse Counts,
    IReadOnlyList<SecurityFindingResponse> Findings);

public sealed record SecurityScanProvenanceResponse(
    string Scanner,
    string Version,
    string Mode,
    string? Range,
    string? ConfigPath,
    string? BaselinePath,
    string ScannedAt);

public sealed record SecurityScanCountsResponse(
    int FilesScanned,
    int NewFindings,
    int AcceptedFindings,
    int BlockFindings,
    int WarnFindings,
    int CleanFiles);

public sealed record SecurityFindingResponse(
    string Id,
    string Aspect,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    IReadOnlyList<SecurityFindingLocationResponse> Locations,
    string Fingerprint,
    string RuleId,
    string? Evidence,
    string Path,
    bool Accepted);

public sealed record SecurityFindingLocationResponse(
    string Path,
    SecurityFindingRangeResponse Range);

public sealed record SecurityFindingRangeResponse(
    SecurityFindingPositionResponse Start,
    SecurityFindingPositionResponse End);

public sealed record SecurityFindingPositionResponse(int Line, int Column);

public sealed record HandoverRequest(
    string FindingSummary,
    string FilePath,
    string FindingText,
    string ReviewKind,
    string MetaReference);

public sealed record ThreadMutationRequest(
    string Path,
    string Kind,
    string? ThreadId,
    string? Body,
    string? ReplyTo,
    string? Status,
    string? HumanName,
    int? Line,
    string? FindingFingerprint,
    string? UnitId = null);

public sealed record FindingStateMutationRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string State,
    string Author,
    string Reason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ExpectedTimestamp,
    string? UnitId = null);

public sealed record FindingSuppressionMutationRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string Author,
    string Reason,
    DateTimeOffset? ExpiresAt,
    long? ExpectedRevision,
    string? UnitId = null);

/// <summary>Per-project outcome of an Agent Studio repository import ("imported", "skipped", or "failed").</summary>
public sealed record AgentStudioImportResultResponse(
    string ProjectId,
    string DisplayName,
    string? RepositoryPath,
    string Status,
    string? RepositoryId,
    string? Reason);

public sealed record AgentStudioImportResponse(
    IReadOnlyList<AgentStudioImportResultResponse> Results,
    int Imported,
    int Skipped,
    int Failed);

/// <summary>Plain status for one side of a run comparison: whether its outcome snapshot could be read.</summary>
public sealed record ReviewRunCompareSnapshotResponse(string RunId, string Status, string? Error);

public sealed record ReviewRunCompareResponse(
    string Status,
    ReviewRunCompareSnapshotResponse Baseline,
    ReviewRunCompareSnapshotResponse Candidate,
    QualityRunComparison? Comparison);

public sealed record ReviewRunPinsResponse(IReadOnlyList<string> PinnedRunIds);

public sealed record ReviewRunRetentionResponse(
    int SnapshotCount, long TotalBytes, long AverageBytes, int PinnedCount, int RetentionKeep);
