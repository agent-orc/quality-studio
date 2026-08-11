using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record TreeResponse(string Path, IReadOnlyList<TreeNodeResponse> Nodes);

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
        string? currentCommit = null)
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
                pair => KindStateResponse.From(node, pair.Value, states),
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

public sealed record KindStateResponse(
    string Direct,
    string Descendants,
    string Overall,
    int? Score,
    string? Band,
    string? MetaPath)
{
    public static KindStateResponse From(
        HierarchyNode node,
        KindAggregation aggregation,
        IReadOnlyDictionary<string, FindingStateRecord> states)
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
                var projected = FindingStateProjection.Apply(metadata, states);
                if (projected["grade"] is JsonObject grade)
                {
                    score = grade["score"]?.GetValue<int>();
                    band = grade["band"]?.GetValue<string>();
                }
            }
        }

        return new(Map(aggregation.Direct), Map(aggregation.Descendants), Map(aggregation.Overall), score, band, metaPath);
    }

    private static string Map(ReviewState state) => state switch
    {
        ReviewState.Current => "fresh",
        ReviewState.Stale => "stale",
        ReviewState.PolicyDrift => "policy-drift",
        _ => "missing",
    };
}

public sealed record FileResponse(
    string Path,
    string Content,
    IReadOnlyList<JsonElement> MetaDocuments,
    long SizeBytes,
    string LineEnding,
    string Encoding,
    CoverageAggregate Coverage);

public sealed record RiskRowResponse(
    string Path,
    string Name,
    int? GradeScore,
    string? GradeBand,
    string ReviewState,
    CoverageAggregate Coverage,
    int Changes,
    decimal? RiskScore);

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
    string? FindingFingerprint);

public sealed record FindingStateMutationRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string State,
    string Author,
    string Reason,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ExpectedTimestamp);

public sealed record FindingAssessmentMutationRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string? Assessment,
    string? Resolution,
    string Actor,
    string Reason,
    long ExpectedRevision,
    string? ReviewRunId,
    string? OperationRunId,
    string? TaskKey);

public sealed record FindingSuppressionMutationRequest(FindingSuppressionRule Rule, long ExpectedRevision);
public sealed record FindingSuppressionPreviewRequest(FindingSuppressionRule Rule);
public sealed record ExactFindingSuppressionRequest(
    string Path,
    string Kind,
    string Fingerprint,
    string Author,
    string Reason,
    DateTimeOffset? ExpiresAt,
    long ExpectedRevision);

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
