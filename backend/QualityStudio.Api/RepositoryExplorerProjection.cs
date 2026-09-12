using System.Security.Cryptography;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// The physical paths of reviewable source files, independent of the five canonical review
/// levels. This projection neither scans the worktree again nor changes a source unit's ID.
/// </summary>
public sealed class RepositoryExplorerProjection
{
    private readonly Dictionary<string, TreeLevelNodeResponse> byId;
    private readonly Dictionary<string, TreeLevelNodeResponse> byPath;
    private readonly Dictionary<string, IReadOnlyList<TreeLevelNodeResponse>> children;

    private RepositoryExplorerProjection(TreeLevelNodeResponse root,
        Dictionary<string, TreeLevelNodeResponse> byId,
        Dictionary<string, TreeLevelNodeResponse> byPath,
        Dictionary<string, IReadOnlyList<TreeLevelNodeResponse>> children)
    {
        Root = root;
        this.byId = byId;
        this.byPath = byPath;
        this.children = children;
    }

    public TreeLevelNodeResponse Root { get; }

    /// <summary>Stable identity shared by physical navigation and directory review scopes.</summary>
    public static string ScopeId(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) normalized = ".";
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { "directory-scope-v1", normalized })));
        return $"qs-v1/generic/namespace/{hash}";
    }

    public TreeLevelNodeResponse? Find(string id) => byId.GetValueOrDefault(id);
    public TreeLevelNodeResponse? FindPath(string path) => byPath.GetValueOrDefault(path);
    public IReadOnlyList<TreeLevelNodeResponse> Children(string? parentId) =>
        parentId is null ? [Root] : children.GetValueOrDefault(parentId) ?? [];

    public IReadOnlyList<TreeLevelNodeResponse> Search(string query) => byPath.Values
        .Where(node => node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       node.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
        .OrderBy(node => node.Path, StringComparer.Ordinal).ToArray();

    public TreeNodeResponse ToTreeNode(TreeLevelNodeResponse node) => new(
        node.Id, node.Name, node.Level, node.Path, node.Kinds, node.FindingsCount,
        node.FindingCounts, node.ReviewedAt, node.SizeBytes, node.LineCount, node.Coverage,
        node.Excluded, Children(node.Id).Select(ToTreeNode).ToArray());

    public static RepositoryExplorerProjection Create(string repositoryRoot,
        IReadOnlyList<HierarchyNode> roots, TreeProjectionIndex canonical,
        IReadOnlyDictionary<string, HierarchyNode>? scopes = null)
    {
        var physicalRoot = new DirectoryEntry(".", Path.GetFileName(Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var directories = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal) { ["."] = physicalRoot };
        // Linked files and aliases retain the same canonical winner as HierarchyUnitResolver.
        var files = Flatten(roots).Where(node => node.Level == ReviewLevel.File)
            .GroupBy(node => node.Path, StringComparer.Ordinal)
            .Select(group => group.OrderBy(node => node.Id, StringComparer.Ordinal).First())
            .OrderBy(node => node.Path, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.Path.Split('/');
            var directory = physicalRoot;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var path = string.Join('/', segments.Take(index + 1));
                if (!directories.TryGetValue(path, out var child))
                {
                    child = new DirectoryEntry(path, segments[index]);
                    directories.Add(path, child);
                    directory.Directories.Add(child);
                }
                directory = child;
            }
            directory.Files.Add(canonical.Get(file) with
            {
                ParentId = ScopeId(directory.Path),
                HasChildren = false,
                ChildCount = 0,
                Children = [],
            });
        }

        var byId = new Dictionary<string, TreeLevelNodeResponse>(StringComparer.Ordinal);
        var byPath = new Dictionary<string, TreeLevelNodeResponse>(StringComparer.Ordinal);
        var children = new Dictionary<string, IReadOnlyList<TreeLevelNodeResponse>>(StringComparer.Ordinal);
        var root = Project(physicalRoot, null);
        return new RepositoryExplorerProjection(root, byId, byPath, children);

        TreeLevelNodeResponse Project(DirectoryEntry directory, string? parentId)
        {
            var id = ScopeId(directory.Path);
            var descendants = directory.Directories.OrderBy(child => child.Name, StringComparer.Ordinal)
                .Select(child => Project(child, id))
                .Concat(directory.Files.OrderBy(file => file.Name, StringComparer.Ordinal)).ToArray();
            foreach (var file in directory.Files)
            {
                byId.Add(file.Id, file);
                byPath.Add(file.Path, file);
            }
            var direct = scopes is not null && scopes.TryGetValue(directory.Path, out var scope)
                ? canonical.GetDirect(scope) : null;
            var kinds = new[] { "code", "security", "performance" }.ToDictionary(kind => kind, kind =>
            {
                var state = WorstState(descendants.Select(child => child.Kinds[kind].Overall));
                var own = direct?.Kinds[kind];
                return new KindStateResponse(own?.Direct ?? "missing", state,
                    WorstState([own?.Direct ?? "missing", state]), own?.Score, own?.Band, own?.MetaPath);
            }, StringComparer.Ordinal);
            var node = new TreeLevelNodeResponse(id, parentId, directory.Name,
                directory.Path == "." ? "repository" : "folder", directory.Path,
                kinds, (direct?.FindingsCount ?? 0) + descendants.Sum(child => child.FindingsCount),
                descendants.Aggregate(direct?.FindingCounts ?? FindingStateCounts.Empty, (counts, child) => counts + child.FindingCounts),
                descendants.Select(child => child.ReviewedAt).Append(direct?.ReviewedAt).Where(value => value is not null)
                    .Order(StringComparer.Ordinal).LastOrDefault(),
                null, null, AggregateCoverage(descendants.Select(child => child.Coverage)),
                direct?.Excluded ?? [], descendants.Length > 0, descendants.Length, []);
            byId.Add(id, node);
            byPath.Add(directory.Path, node);
            children.Add(id, descendants);
            return node;
        }
    }

    private static CoverageAggregate AggregateCoverage(IEnumerable<CoverageAggregate> children)
    {
        var known = children.Where(child => child.State != "unknown").ToArray();
        if (known.Length == 0) return CoverageAggregate.Unknown;
        var coveredLines = known.Sum(child => child.CoveredLines);
        var totalLines = known.Sum(child => child.TotalLines);
        var coveredBranches = known.Sum(child => child.CoveredBranches);
        var totalBranches = known.Sum(child => child.TotalBranches);
        return new CoverageAggregate(known.Any(child => child.State == "stale") ? "stale" : "current",
            coveredLines, totalLines, coveredBranches, totalBranches,
            Percent(coveredLines, totalLines), Percent(coveredBranches, totalBranches),
            known[0].Commit, known[0].MeasuredAt, known.Sum(child => child.FilesWithData), null, null);
    }

    private static decimal? Percent(int covered, int total) =>
        total == 0 ? null : Math.Round(covered * 100m / total, 2, MidpointRounding.AwayFromZero);

    private static string WorstState(IEnumerable<string> states) => states
        .OrderByDescending(state => state switch
        {
            "invalid" => 4,
            "stale" => 3,
            "policy-drift" => 2,
            "fresh" => 1,
            _ => 0,
        }).FirstOrDefault() ?? "missing";

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private sealed class DirectoryEntry(string path, string name)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public List<DirectoryEntry> Directories { get; } = [];
        public List<TreeLevelNodeResponse> Files { get; } = [];
    }
}
