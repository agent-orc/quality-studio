using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Where a review-meta sidecar lives. The runner writes at this path, the staleness scanner
/// recognises an unreadable sidecar by it, and the secret scanner replaces the one it owns, so the
/// convention exists once rather than in each of them.
/// </summary>
public static class ReviewMetaPath
{
    public static string For(string repositoryRoot, string subjectFile, string relativePath, ReviewLevel level, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var directory = level switch
        {
            ReviewLevel.Project => repositoryRoot,
            ReviewLevel.Module when File.Exists(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                => Path.GetDirectoryName(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))!,
            _ => Path.GetDirectoryName(subjectFile)!,
        };
        var lane = level switch
        {
            ReviewLevel.File => "files",
            ReviewLevel.Namespace => "namespaces",
            _ => string.Empty,
        };
        var prefix = level.ToString().ToLowerInvariant();
        return Path.Combine(directory, ".quality", "reviews", lane,
            $"{prefix}.{Key(relativePath)}.review-meta.{kind}.json");
    }

    /// <summary>The sidecar a File-level review of this repository-relative path writes.</summary>
    public static string ForFile(string repositoryRoot, string relativePath, string kind) =>
        For(repositoryRoot,
            Path.Combine(Path.GetFullPath(repositoryRoot), relativePath.Replace('/', Path.DirectorySeparatorChar)),
            relativePath,
            ReviewLevel.File,
            kind);

    private static string Key(string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
}
