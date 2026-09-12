using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>
/// Physical directories are independent review scopes. Canonical file identities remain unchanged,
/// and a source file shared by several language namespaces contributes once to each directory.
/// </summary>
public static class DirectoryReviewScopes
{
    public static IReadOnlyDictionary<string, HierarchyNode> Build(IEnumerable<HierarchyNode> roots)
    {
        var nodes = Flatten(roots).ToArray();
        var scopes = new Dictionary<string, HierarchyNode>(StringComparer.Ordinal);
        foreach (var file in nodes.Where(node => node.Level == ReviewLevel.File)
                     .OrderBy(node => node.Id, StringComparer.Ordinal)
                     .DistinctBy(node => node.Path, StringComparer.Ordinal))
        {
            var path = file.Path.Replace('\\', '/');
            var directory = path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
            while (true)
            {
                if (!scopes.TryGetValue(directory, out var scope))
                {
                    scope = new HierarchyNode(RepositoryExplorerProjection.ScopeId(directory),
                        directory == "." ? "Repository" : directory[(directory.LastIndexOf('/') + 1)..],
                        ReviewLevel.Namespace, directory);
                    scopes.Add(directory, scope);
                }
                scope.AddChild(file);
                if (directory == ".") break;
                directory = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : ".";
            }
        }

        var exclusions = nodes.SelectMany(node => node.Exclusions).Distinct().ToArray();
        foreach (var (path, scope) in scopes)
            scope.AddExclusions(exclusions.Where(exclusion => path == "." ||
                exclusion.Path == path || exclusion.Path.StartsWith(path + "/", StringComparison.Ordinal)));
        return scopes;
    }

    public static IReadOnlyDictionary<string, HierarchyNode> Load(
        string root, IEnumerable<HierarchyNode> roots, InputResolver inputResolver,
        string? globalInputsDirectory, int inputBudgetCharacters)
    {
        var scopes = Build(roots);
        var ids = scopes.Values.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        ReviewMetaDiscovery.AttachDiscovered(root, scopes.Values, inputResolver,
            globalInputsDirectory, inputBudgetCharacters, node => ids.Contains(node.Id));
        return scopes;
    }

    private static IEnumerable<HierarchyNode> Flatten(IEnumerable<HierarchyNode> roots)
    {
        foreach (var node in roots)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
