namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Resolves logical <c>.quality/**</c> artifacts either below an explicit external project data
/// directory or, for backwards-compatible library callers, below the repository checkout.
/// </summary>
public static class QualityDataPaths
{
    public static string Resolve(string repositoryRoot, string? dataRoot, string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        var normalized = logicalPath.Replace('\\', '/').TrimStart('/');
        if (normalized.Equals(".quality", StringComparison.Ordinal)) normalized = string.Empty;
        else if (normalized.StartsWith(".quality/", StringComparison.Ordinal)) normalized = normalized[9..];
        var root = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Path.GetFullPath(repositoryRoot), ".quality")
            : Path.GetFullPath(dataRoot);
        return normalized.Length == 0
            ? root
            : Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }
}
