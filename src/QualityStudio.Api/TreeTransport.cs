using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record TreeLevelResponse(
    int SchemaVersion,
    string? ParentId,
    string Path,
    string SnapshotEtag,
    int Offset,
    int Limit,
    string? NextCursor,
    IReadOnlyList<TreeLevelNodeResponse> Nodes);

public sealed record TreeLevelNodeResponse(
    string Id,
    string? ParentId,
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
    bool HasChildren,
    int ChildCount,
    IReadOnlyList<TreeLevelNodeResponse> Children);

/// <summary>
/// Builds the facts shared by lazy tree pages in one post-order pass. The hierarchy remains
/// immutable; this request projection prevents each response node from walking its descendants
/// again for rolled-up review, finding, and coverage facts.
/// </summary>
public sealed class TreeProjectionIndex
{
    private readonly Dictionary<HierarchyNode, NodeFacts> facts;

    private TreeProjectionIndex(Dictionary<HierarchyNode, NodeFacts> facts) => this.facts = facts;

    public static TreeProjectionIndex Create(
        IReadOnlyList<HierarchyNode> roots,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        CoverageSnapshot? coverage,
        string? currentCommit)
    {
        var facts = new Dictionary<HierarchyNode, NodeFacts>(ReferenceEqualityComparer.Instance);
        var coverageFiles = (coverage?.Files ?? [])
            .GroupBy(file => NormalizePath(file.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var root in roots)
        {
            Build(root, parentId: null, states, coverage, currentCommit, coverageFiles, facts);
        }

        return new TreeProjectionIndex(facts);
    }

    public TreeLevelNodeResponse Get(HierarchyNode node)
    {
        var fact = facts[node];
        return new TreeLevelNodeResponse(
            node.Id,
            fact.ParentId,
            node.Name,
            node.Level.ToString().ToLowerInvariant(),
            node.Path,
            fact.Kinds,
            fact.FindingsCount,
            fact.FindingCounts,
            fact.ReviewedAt?.ToUniversalTime().ToString("O"),
            node.SizeBytes,
            node.LineCount,
            fact.Coverage,
            node.Exclusions.OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Reason, StringComparer.Ordinal)
                .Select(item => new ScopeExclusionResponse(item.Path, item.Reason)).ToArray(),
            node.Children.Count > 0,
            node.Children.Count,
            []);
    }

    private static NodeFacts Build(
        HierarchyNode node,
        string? parentId,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        CoverageSnapshot? coverage,
        string? currentCommit,
        IReadOnlyDictionary<string, CoverageFile[]> coverageFiles,
        IDictionary<HierarchyNode, NodeFacts> facts)
    {
        var children = node.Children.Select(child =>
            Build(child, node.Id, states, coverage, currentCommit, coverageFiles, facts)).ToArray();
        var aggregations = Enum.GetValues<ReviewKind>().ToDictionary(kind => kind, kind =>
        {
            var direct = node.Documents.TryGetValue(kind, out var document)
                ? document.State
                : ReviewState.NotReviewed;
            var descendants = HierarchyAggregation.WorstOf(children.Select(child => child.Aggregations[kind].Overall));
            return new KindAggregation(kind, direct, descendants,
                HierarchyAggregation.WorstOf([direct, descendants]));
        });
        var kinds = aggregations.ToDictionary(
            pair => pair.Key.ToString().ToLowerInvariant(),
            pair => KindStateResponse.From(node, pair.Value, states),
            StringComparer.Ordinal);

        var directSummary = DirectReviewSummary(node, states);
        var counts = children.Aggregate(directSummary.Counts, (current, child) => current + child.FindingCounts);
        var findingsCount = directSummary.FindingsCount + children.Sum(child => child.FindingsCount);
        var reviewedAt = children.Select(child => child.ReviewedAt)
            .Where(value => value is not null)
            .Append(directSummary.ReviewedAt)
            .Where(value => value is not null)
            .Max();
        if (node.Level == ReviewLevel.File)
        {
            counts = counts with
            {
                Resolved = states.Values.Count(state => state.State == FindingState.Resolved &&
                    string.Equals(state.Path, node.Path, StringComparison.Ordinal)),
            };
            findingsCount = counts.Visible;
        }

        var coveragePaths = new HashSet<string>(StringComparer.Ordinal);
        if (node.Level == ReviewLevel.File) coveragePaths.Add(NormalizePath(node.Path));
        foreach (var child in children) coveragePaths.UnionWith(child.CoveragePaths);
        var coverageAggregate = ProjectCoverage(
            coverage, currentCommit, coverageFiles, coveragePaths, node.Level == ReviewLevel.File);

        var result = new NodeFacts(parentId, aggregations, kinds, findingsCount, counts,
            reviewedAt, coveragePaths, coverageAggregate);
        facts[node] = result;
        return result;
    }

    private static (int FindingsCount, FindingStateCounts Counts, DateTimeOffset? ReviewedAt) DirectReviewSummary(
        HierarchyNode node,
        IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var findingsCount = 0;
        var counts = FindingStateCounts.Empty;
        DateTimeOffset? reviewedAt = null;
        foreach (var document in node.Documents.Values)
        {
            if (document.Payload is null) continue;
            var metadata = JsonNode.Parse(document.Payload)!.AsObject();
            findingsCount += metadata["findings"]?.AsArray().Count ?? 0;
            counts += FindingStateProjection.Count(metadata, states);
            if (metadata["reviewedAt"]?.GetValue<string>() is { } value &&
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var candidate) &&
                (reviewedAt is null || candidate > reviewedAt))
            {
                reviewedAt = candidate;
            }
        }

        return (findingsCount, counts, reviewedAt);
    }

    private static CoverageAggregate ProjectCoverage(
        CoverageSnapshot? snapshot,
        string? currentCommit,
        IReadOnlyDictionary<string, CoverageFile[]> coverageFiles,
        IReadOnlySet<string> paths,
        bool file)
    {
        if (snapshot is null) return CoverageAggregate.Unknown;
        var matches = paths.Where(coverageFiles.ContainsKey).SelectMany(path => coverageFiles[path]).ToArray();
        if (matches.Length == 0) return CoverageAggregate.Unknown;
        var coveredLines = matches.Sum(item => item.CoveredLines);
        var totalLines = matches.Sum(item => item.TotalLines);
        var coveredBranches = matches.Sum(item => item.CoveredBranches);
        var totalBranches = matches.Sum(item => item.TotalBranches);
        var state = !string.IsNullOrWhiteSpace(snapshot.Commit) &&
                    !string.IsNullOrWhiteSpace(currentCommit) &&
                    !StringComparer.Ordinal.Equals(snapshot.Commit, currentCommit)
            ? "stale"
            : "current";
        var single = file ? matches[0] : null;
        return new CoverageAggregate(
            state,
            coveredLines,
            totalLines,
            coveredBranches,
            totalBranches,
            Percent(coveredLines, totalLines),
            Percent(coveredBranches, totalBranches),
            snapshot.Commit,
            snapshot.MeasuredAt,
            matches.Length,
            single?.UncoveredLines,
            single?.UncoveredBranchLines);
    }

    private static decimal? Percent(int covered, int total) =>
        total == 0 ? null : Math.Round(covered * 100m / total, 2, MidpointRounding.AwayFromZero);

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('.', '/');

    private sealed record NodeFacts(
        string? ParentId,
        IReadOnlyDictionary<ReviewKind, KindAggregation> Aggregations,
        IReadOnlyDictionary<string, KindStateResponse> Kinds,
        int FindingsCount,
        FindingStateCounts FindingCounts,
        DateTimeOffset? ReviewedAt,
        IReadOnlySet<string> CoveragePaths,
        CoverageAggregate Coverage);
}

/// <summary>Retains exactly one derived tree projection per repository and hierarchy state.</summary>
public sealed record TreeProjectionMeasurement(TreeProjectionIndex Index, bool CacheHit);

public sealed class TreeProjectionCache
{
    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);

    public TreeProjectionIndex Get(
        string repositoryRoot,
        RepositoryHierarchySnapshot snapshot,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        CoverageSnapshot? coverage,
        string? currentCommit) =>
        GetMeasured(repositoryRoot, snapshot, states, coverage, currentCommit).Index;

    public TreeProjectionMeasurement GetMeasured(
        string repositoryRoot,
        RepositoryHierarchySnapshot snapshot,
        IReadOnlyDictionary<string, FindingStateRecord> states,
        CoverageSnapshot? coverage,
        string? currentCommit)
    {
        var root = System.IO.Path.GetFullPath(repositoryRoot);
        var statePath = new FindingStateStore(root).StatePath;
        var stateInfo = new FileInfo(statePath);
        var key = string.Join('\0', snapshot.GitState, coverage?.MeasuredAt, currentCommit,
            stateInfo.Exists ? stateInfo.Length : 0,
            stateInfo.Exists ? stateInfo.LastWriteTimeUtc.Ticks : 0);
        var slot = slots.GetOrAdd(root, _ => new CacheSlot());
        lock (slot.Gate)
        {
            if (slot.Index is not null && StringComparer.Ordinal.Equals(slot.Key, key))
                return new TreeProjectionMeasurement(slot.Index, true);
            slot.Key = key;
            slot.Index = TreeProjectionIndex.Create(snapshot.Roots, states, coverage, currentCommit);
            return new TreeProjectionMeasurement(slot.Index, false);
        }
    }

    private sealed class CacheSlot
    {
        public object Gate { get; } = new();
        public string? Key { get; set; }
        public TreeProjectionIndex? Index { get; set; }
    }
}

public sealed class MeasuredTreeJsonResult(
    TreeLevelResponse payload,
    long requestStarted,
    double snapshotMilliseconds,
    double projectionMilliseconds,
    string repositoryId,
    ILogger logger) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var serializationStarted = Stopwatch.GetTimestamp();
        var options = context.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        var serializationMilliseconds = Stopwatch.GetElapsedTime(serializationStarted).TotalMilliseconds;
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        context.Response.Headers["Server-Timing"] = FormattableString.Invariant(
            $"tree-snapshot;dur={snapshotMilliseconds:F2}, tree-projection;dur={projectionMilliseconds:F2}, tree-serialization;dur={serializationMilliseconds:F2}");
        context.Response.OnCompleted(() =>
        {
            var totalMilliseconds = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
            logger.LogInformation(new EventId(1101, "TreeTransportCompleted"),
                "{TreeTransportEvent}", JsonSerializer.Serialize(new
                {
                    @event = "qs.tree.transport",
                    schemaVersion = payload.SchemaVersion,
                    repositoryId,
                    payload.ParentId,
                    payload.Path,
                    nodeCount = payload.Nodes.Count,
                    responseBytes = body.Length,
                    snapshotMs = Math.Round(snapshotMilliseconds, 2),
                    projectionMs = Math.Round(projectionMilliseconds, 2),
                    serializationMs = Math.Round(serializationMilliseconds, 2),
                    totalMs = Math.Round(totalMilliseconds, 2),
                }));
            return Task.CompletedTask;
        });
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }
}
