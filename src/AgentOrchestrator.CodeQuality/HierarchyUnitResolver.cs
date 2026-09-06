namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Answers "which unit ID belongs to this path" from the cached repository hierarchy.
/// Building the hierarchy reads every source file in the repository, so callers that ask once
/// per review — the review runner and the secret scanner — must share one snapshot rather than
/// rebuild it. <see cref="Shared"/> serves callers without a container; hosts that already own a
/// <see cref="RepositoryHierarchyCache"/> pass it in so one cache serves the whole process.
/// </summary>
public sealed class HierarchyUnitResolver
{
    public static HierarchyUnitResolver Shared { get; } = new();

    private readonly RepositoryHierarchyCache cache;

    public HierarchyUnitResolver(RepositoryHierarchyCache? cache = null) =>
        this.cache = cache ?? new RepositoryHierarchyCache();

    /// <summary>The canonical unit ID for a repository-relative path at one level, if it derives.</summary>
    public string? ResolveUnitId(string repositoryRoot, string relativePath, ReviewLevel level) =>
        Units(repositoryRoot)
            .Where(node => node.Level == level && StringComparer.Ordinal.Equals(node.Path, relativePath))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Select(node => node.Id)
            .FirstOrDefault();

    /// <summary>The canonical File unit per repository-relative path.</summary>
    public IReadOnlyDictionary<string, HierarchyNode> FileUnitsByPath(string repositoryRoot) =>
        Units(repositoryRoot)
            .Where(node => node.Level == ReviewLevel.File)
            .GroupBy(node => node.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(node => node.Id, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);

    private IEnumerable<HierarchyNode> Units(string repositoryRoot) => Flatten(cache.Get(repositoryRoot).Roots);

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
