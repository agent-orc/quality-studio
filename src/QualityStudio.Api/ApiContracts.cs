using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record TreeResponse(string Path, IReadOnlyList<TreeNodeResponse> Nodes);

public sealed record TreeNodeResponse(
    string Id,
    string Name,
    string Level,
    string Path,
    IReadOnlyDictionary<string, KindStateResponse> Kinds,
    int FindingsCount,
    string? ReviewedAt,
    long? SizeBytes,
    int? LineCount,
    IReadOnlyList<TreeNodeResponse> Children)
{
    public static TreeNodeResponse From(HierarchyNode node)
    {
        var reviewSummary = DirectReviewSummary.From(node);
        return new(
            node.Id,
            node.Name,
            node.Level.ToString().ToLowerInvariant(),
            node.Path,
            node.AggregatedStates.ToDictionary(
                pair => pair.Key.ToString().ToLowerInvariant(),
                pair => KindStateResponse.From(node, pair.Value),
                StringComparer.Ordinal),
            reviewSummary.FindingsCount,
            reviewSummary.ReviewedAt,
            node.SizeBytes,
            node.LineCount,
            node.Children.Select(From).ToArray());
    }

    private sealed record DirectReviewSummary(int FindingsCount, string? ReviewedAt)
    {
        public static DirectReviewSummary From(HierarchyNode node)
        {
            var findingsCount = 0;
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

                if (root.TryGetProperty("reviewedAt", out var reviewedAtElement) &&
                    reviewedAtElement.TryGetDateTimeOffset(out var candidate) &&
                    (reviewedAt is null || candidate > reviewedAt))
                {
                    reviewedAt = candidate;
                }
            }

            return new(findingsCount, reviewedAt?.ToString("O"));
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
    public static KindStateResponse From(HierarchyNode node, KindAggregation aggregation)
    {
        int? score = null;
        string? band = null;
        string? metaPath = null;
        if (node.Documents.TryGetValue(aggregation.Kind, out var document))
        {
            metaPath = document.SourcePath;
            if (document.Payload is not null)
            {
                using var json = JsonDocument.Parse(document.Payload);
                if (json.RootElement.TryGetProperty("grade", out var grade))
                {
                    score = grade.TryGetProperty("score", out var scoreElement) ? scoreElement.GetInt32() : null;
                    band = grade.TryGetProperty("band", out var bandElement) ? bandElement.GetString() : null;
                }
            }
        }

        return new(Map(aggregation.Direct), Map(aggregation.Descendants), Map(aggregation.Overall), score, band, metaPath);
    }

    private static string Map(ReviewState state) => state switch
    {
        ReviewState.Current => "fresh",
        ReviewState.Stale => "stale",
        _ => "missing",
    };
}

public sealed record FileResponse(
    string Path,
    string Content,
    IReadOnlyList<JsonElement> MetaDocuments,
    long SizeBytes,
    string LineEnding,
    string Encoding);

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
