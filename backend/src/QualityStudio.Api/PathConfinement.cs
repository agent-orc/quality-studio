namespace QualityStudio.Api;

internal static class PathConfinement
{
    public static bool IsWithin(string root, string candidate, bool allowRoot = true)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowRoot && string.Equals(root, candidate, PathComparison)) return true;
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    public static void RejectReparseTraversal(string root, string candidate)
    {
        if (!IsWithin(root, candidate)) throw new ArgumentException("Path escapes its configured root.");
        var relative = Path.GetRelativePath(root, candidate);
        var current = Path.GetFullPath(root);
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Paths cannot traverse symbolic links or junctions.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
