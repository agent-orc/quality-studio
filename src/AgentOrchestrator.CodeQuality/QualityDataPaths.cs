namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Keeps repository source paths separate from Quality Studio's mutable project data.
/// A data root mirrors the repository layout, so legacy paths such as
/// <c>src/.quality/reviews</c> retain their stable relative representation.
/// </summary>
public static class QualityDataPaths
{
    public static string Root(string repositoryRoot, string? dataRoot) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? repositoryRoot : dataRoot);

    public static string ResolveMutablePath(string repositoryRoot, string? dataRoot, string relativePath)
    {
        var canonical = relativePath.Replace('\\', '/').TrimStart('/');
        var storageRoot = Root(repositoryRoot, dataRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var useStorage = canonical.Equals(".quality", StringComparison.Ordinal) ||
                         canonical.StartsWith(".quality/", StringComparison.Ordinal) ||
                         canonical.Contains("/.quality/", StringComparison.Ordinal);
        return Path.GetFullPath(Path.Combine(useStorage ? storageRoot : root,
            canonical.Replace('/', Path.DirectorySeparatorChar)));
    }
}
