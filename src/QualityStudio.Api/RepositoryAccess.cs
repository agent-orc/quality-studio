using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>Confines repository operations to one immutable registry entry.</summary>
public sealed class RepositoryAccess
{
    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };
    private readonly string root;

    public RepositoryAccess(string root)
    {
        this.root = Path.GetFullPath(root);
        if (!Directory.Exists(this.root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {this.root}");
        }
    }

    public string Root => root;

    public string NormalizeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return ".";
        }

        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException("Path must be repository-relative.", nameof(path));
        }

        var absolute = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path escapes the selected repository root.", nameof(path));
        }

        RejectReparseTraversal(absolute);

        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }

    public string ResolveFile(string? path)
    {
        var relative = NormalizeRelativePath(path);
        if (relative == ".")
        {
            throw new ArgumentException("A file path is required.", nameof(path));
        }

        var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            throw new FileNotFoundException($"Repository file was not found: {relative}", relative);
        }

        return absolute;
    }

    public IReadOnlyList<JsonElement> ReadMetaDocuments(string relativePath)
    {
        var result = new List<JsonElement>();
        foreach (var candidate in Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                     .Where(candidate => candidate.Contains(".review-meta.", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(candidate));
            if (document.RootElement.TryGetProperty("unit", out var unit) &&
                unit.TryGetProperty("path", out var unitPath) &&
                string.Equals(NormalizeStoredPath(unitPath.GetString()), relativePath, StringComparison.Ordinal))
            {
                result.Add(document.RootElement.Clone());
            }
        }

        return result;
    }

    public string FindMetaDocument(string relativePath, string kind)
    {
        var normalized = NormalizeRelativePath(relativePath);
        foreach (var candidate in Directory.EnumerateFiles(root, $"*.review-meta.{kind}.json", ConfinedEnumeration))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(candidate));
            if (document.RootElement.TryGetProperty("unit", out var unit) &&
                unit.TryGetProperty("path", out var unitPath) &&
                string.Equals(NormalizeStoredPath(unitPath.GetString()), normalized, StringComparison.Ordinal)) return candidate;
        }
        throw new FileNotFoundException($"No {kind} review metadata exists for '{normalized}'.", normalized);
    }

    private static string? NormalizeStoredPath(string? path) => path?.Replace('\\', '/').TrimStart('/');

    private void RejectReparseTraversal(string absolute)
    {
        var relative = Path.GetRelativePath(root, absolute);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException("Repository paths cannot traverse symbolic links or junctions.", nameof(absolute));
            }
        }
    }
}
