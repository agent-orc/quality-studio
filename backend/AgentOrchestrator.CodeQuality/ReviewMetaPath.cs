using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Where a review-meta sidecar lives. The runner writes at this path, the staleness scanner
/// recognises an unreadable sidecar by it, and the secret scanner replaces the one it owns, so the
/// convention exists once rather than in each of them.
/// <para>
/// Sidecars are generated data and live under the project's data root, not next to the reviewed
/// file. The directory of the reviewed subject is still mirrored below <c>reviews/</c>, so the
/// layout stays browsable and a sidecar remains attributable to its folder without opening it.
/// </para>
/// </summary>
public static class ReviewMetaPath
{
    /// <summary>The folder below a project's data root that holds every review sidecar.</summary>
    public const string LaneRoot = "reviews";

    public static string For(string repositoryRoot, string subjectFile, string relativePath, ReviewLevel level, string kind,
        bool directoryScope = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var root = Path.GetFullPath(repositoryRoot);
        if (directoryScope && level != ReviewLevel.Namespace)
            throw new ArgumentException("A directory scope uses the namespace aggregate review level.", nameof(level));
        // Directory identities outlive changes to the first member. Keep their sidecars anchored
        // to the directory itself and separate from canonical namespaces at the same source path.
        var subjectDirectory = directoryScope
            ? Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            : level switch
            {
                ReviewLevel.Project => root,
                ReviewLevel.Module when File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                    => Path.GetDirectoryName(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))!,
                _ => Path.GetDirectoryName(Path.GetFullPath(subjectFile))!,
            };
        var lane = directoryScope ? "directories" : level switch
        {
            ReviewLevel.File => "files",
            ReviewLevel.Namespace => "namespaces",
            _ => string.Empty,
        };
        var prefix = level.ToString().ToLowerInvariant();
        return Path.Combine(
            QualityDataRoot.Combine(root, LaneRoot),
            MirroredDirectory(root, subjectDirectory),
            lane,
            $"{prefix}.{Key(relativePath)}.review-meta.{kind}.json");
    }

    /// <summary>
    /// The subject directory as a path below <c>reviews/</c>. A subject outside the checkout - a
    /// review driven from a different working directory - mirrors nothing and files at the top,
    /// where the hashed key still keeps it unique.
    /// </summary>
    private static string MirroredDirectory(string root, string subjectDirectory)
    {
        var relative = Path.GetRelativePath(root, subjectDirectory);
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? string.Empty
            : relative;
    }

    /// <summary>The sidecar a File-level review of this repository-relative path writes.</summary>
    public static string ForFile(string repositoryRoot, string relativePath, string kind) =>
        For(repositoryRoot,
            Path.Combine(Path.GetFullPath(repositoryRoot), relativePath.Replace('/', Path.DirectorySeparatorChar)),
            relativePath,
            ReviewLevel.File,
            kind);

    /// <summary>The folder holding every review sidecar of one project, whether or not it exists.</summary>
    public static string LaneRootFor(string repositoryRoot) =>
        QualityDataRoot.Combine(repositoryRoot, LaneRoot);

    /// <summary>
    /// How a sidecar is named in anything a reader sees: its position below the lane, with the lane
    /// in front, using forward slashes. Reports used to name sidecars relative to the checkout; from
    /// there a sidecar in the data root is only reachable as a run of <c>..</c> segments, which names
    /// nothing stable and differs per machine.
    /// </summary>
    public static string Describe(string repositoryRoot, string sidecarPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        return LaneRoot + "/" +
               Path.GetRelativePath(LaneRootFor(repositoryRoot), sidecarPath).Replace('\\', '/');
    }

    /// <summary>
    /// Every sidecar of one project, optionally of one kind. Discovery went through a recursive
    /// <c>*.json</c> glob of the whole checkout in four places; now that sidecars have a folder of
    /// their own, the walk is confined to it and the convention keeps owning the layout.
    /// </summary>
    public static IEnumerable<string> Enumerate(string repositoryRoot, string? kind = null)
    {
        var lane = LaneRootFor(repositoryRoot);
        if (!Directory.Exists(lane)) return [];
        var pattern = string.IsNullOrWhiteSpace(kind) ? "*.review-meta.*.json" : $"*.review-meta.{kind}.json";
        return Directory.EnumerateFiles(lane, pattern, ConfinedEnumeration);
    }

    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static string Key(string relativePath) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
}
