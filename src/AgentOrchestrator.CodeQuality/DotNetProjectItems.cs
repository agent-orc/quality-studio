using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Resolves solution membership and <c>Compile</c> items for MSBuild projects without running an
/// MSBuild evaluation. The rules honoured and the rules deliberately skipped are documented in
/// <c>docs/hierarchy-derivation.md</c>; nothing here guesses beyond the project files it reads.
/// </summary>
internal static class DotNetProjectItems
{
    /// <summary>
    /// Directory names never walked while collecting default compile items. <c>bin</c> and
    /// <c>obj</c> mirror MSBuild's <c>DefaultItemExcludes</c>; the remainder are tool directories
    /// that never hold checked-in C# sources.
    /// </summary>
    private static readonly string[] PrunedDirectories =
        ["bin", "obj", ".git", ".vs", ".quality", "node_modules"];

    private static readonly EnumerationOptions TopLevelEnumeration = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Every project file below <paramref name="root"/>, skipping build output directories.</summary>
    public static IReadOnlyList<string> DiscoverProjectFiles(string root) =>
        EnumerateDirectories(root, NoOwnedDirectories())
            .SelectMany(directory => SafeFiles(directory, "*.csproj"))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Project files referenced by a solution. <c>.slnx</c> is parsed as XML; <c>.sln</c> project
    /// lines are parsed by splitting their quoted fields rather than by matching a regular
    /// expression over the whole line. Non-C# project types are skipped because this adapter only
    /// derives C# units.
    /// </summary>
    public static IReadOnlyList<string> ReadSolutionProjects(string solutionPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var declared = Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadSolutionXmlProjects(solutionPath)
            : ReadClassicSolutionProjects(solutionPath);
        return declared
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(path.Replace('\\', '/'), directory))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The <c>Compile</c> items of one project as absolute paths. Default items are the recursive
    /// <c>**/*.cs</c> glob below the project directory minus build output and minus directories
    /// owned by a nearer project; the project body's <c>Compile</c> <c>Include</c> and
    /// <c>Remove</c> elements are then applied in document order, which is the order MSBuild
    /// applies them in after the SDK's default item props.
    /// </summary>
    public static IReadOnlyList<string> ResolveCompileItems(
        string root, string projectFile, IReadOnlyCollection<string> allProjectFiles)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch (System.Xml.XmlException)
        {
            // An unreadable project file is a diagnostic, not a guessed member list.
            return [];
        }

        var items = new HashSet<string>(PathComparer);
        if (UsesDefaultCompileItems(document))
        {
            var owned = OwnedProjectDirectories(projectDirectory, allProjectFiles);
            foreach (var file in EnumerateDirectories(projectDirectory, owned)
                         .SelectMany(directory => SafeFiles(directory, "*.cs")))
            {
                items.Add(file);
            }
        }

        foreach (var element in document.Descendants().Where(element => element.Name.LocalName == "Compile"))
        {
            ApplyCompileElement(root, projectDirectory, element, items);
        }

        return items.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> ReadSolutionXmlProjects(string solutionPath) =>
        XDocument.Load(solutionPath).Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!);

    private static IEnumerable<string> ReadClassicSolutionProjects(string solutionPath)
    {
        foreach (var line in File.ReadLines(solutionPath))
        {
            var trimmed = line.AsSpan().TrimStart();
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            // A project line reads `Project("{guid}") = "name", "relative\path.csproj", "{guid}"`.
            // The second quoted field after the assignment is the project path.
            var fields = QuotedFields(trimmed[(separator + 1)..].ToString());
            if (fields.Count >= 2)
            {
                yield return fields[1];
            }
        }
    }

    private static List<string> QuotedFields(string value)
    {
        var fields = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var opening = value.IndexOf('"', index);
            if (opening < 0)
            {
                break;
            }

            var closing = value.IndexOf('"', opening + 1);
            if (closing < 0)
            {
                break;
            }

            fields.Add(value[(opening + 1)..closing]);
            index = closing + 1;
        }

        return fields;
    }

    private static void ApplyCompileElement(
        string root, string projectDirectory, XElement element, HashSet<string> items)
    {
        var include = element.Attribute("Include")?.Value;
        if (!string.IsNullOrWhiteSpace(include))
        {
            foreach (var pattern in SplitList(include))
            {
                foreach (var file in EnumerateGlob(projectDirectory, pattern))
                {
                    // A compile item outside the repository is a diagnostic, not hierarchy.
                    if (IsWithin(root, file))
                    {
                        items.Add(file);
                    }
                }
            }
        }

        var remove = element.Attribute("Remove")?.Value;
        if (string.IsNullOrWhiteSpace(remove))
        {
            return;
        }

        foreach (var pattern in SplitList(remove))
        {
            var matcher = GlobMatcher.Create(projectDirectory, pattern);
            items.RemoveWhere(matcher.IsMatch);
        }
    }

    /// <summary>
    /// <c>EnableDefaultCompileItems</c> and <c>EnableDefaultItems</c> from the project body.
    /// Conditions are not evaluated: the last occurrence wins.
    /// </summary>
    private static bool UsesDefaultCompileItems(XDocument document)
    {
        var enabled = true;
        foreach (var element in document.Descendants()
                     .Where(element => element.Name.LocalName is "EnableDefaultItems" or "EnableDefaultCompileItems"))
        {
            if (bool.TryParse(element.Value.Trim(), out var value))
            {
                enabled = value;
            }
        }

        return enabled;
    }

    /// <summary>
    /// Directories of projects nested below <paramref name="projectDirectory"/>. The nearest
    /// project ancestor of a file wins, so those subtrees are not walked for the outer project.
    /// </summary>
    private static HashSet<string> OwnedProjectDirectories(
        string projectDirectory, IReadOnlyCollection<string> allProjectFiles)
    {
        var owned = new HashSet<string>(PathComparer);
        foreach (var other in allProjectFiles)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(other))!;
            if (!PathComparer.Equals(directory, projectDirectory) && IsWithin(projectDirectory, directory))
            {
                owned.Add(directory);
            }
        }

        return owned;
    }

    /// <summary>
    /// Files matching one explicit item glob. An explicit <c>Include</c> wins over the nested
    /// project rule, so this walk prunes only build output.
    /// </summary>
    private static IEnumerable<string> EnumerateGlob(string baseDirectory, string pattern)
    {
        var segments = Normalize(pattern).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fixedSegments = segments.TakeWhile(segment => !ContainsWildcard(segment)).ToArray();
        var literalBase = Anchor(baseDirectory, fixedSegments);
        if (fixedSegments.Length == segments.Length)
        {
            return File.Exists(literalBase) ? [literalBase] : [];
        }

        var matcher = GlobMatcher.Create(baseDirectory, pattern);
        return EnumerateDirectories(literalBase, NoOwnedDirectories())
            .SelectMany(directory => SafeFiles(directory, "*"))
            .Where(matcher.IsMatch);
    }

    private static string Anchor(string baseDirectory, string[] fixedSegments) =>
        fixedSegments.Length == 0
            ? Path.GetFullPath(baseDirectory)
            : Path.GetFullPath(string.Join('/', fixedSegments), baseDirectory);

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool ContainsWildcard(string value) => value.Contains('*') || value.Contains('?');

    private static HashSet<string> NoOwnedDirectories() => new(PathComparer);

    /// <summary>Iterative directory walk that never descends into build output or owned projects.</summary>
    private static IEnumerable<string> EnumerateDirectories(string start, HashSet<string> ownedDirectories)
    {
        if (!Directory.Exists(start))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            string[] children;
            try
            {
                children = Directory.GetDirectories(current, "*", TopLevelEnumeration);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (PrunedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase) ||
                    ownedDirectories.Contains(child))
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static string[] SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern, TopLevelEnumeration);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static bool IsWithin(string directory, string candidate)
    {
        var normalizedDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(candidate)
            .StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Normalize(string value) => value.Replace('\\', '/').Trim();

    /// <summary>
    /// MSBuild item glob semantics on absolute paths: <c>**</c> crosses directories, <c>*</c> and
    /// <c>?</c> do not, a trailing <c>/**</c> means every file below that directory, and matching
    /// follows the filesystem's case rules.
    /// </summary>
    private sealed class GlobMatcher
    {
        private readonly Regex pattern;

        private GlobMatcher(Regex pattern) => this.pattern = pattern;

        public static GlobMatcher Create(string baseDirectory, string glob)
        {
            var normalized = Normalize(glob);
            if (normalized.EndsWith("/**", StringComparison.Ordinal) ||
                normalized.Equals("**", StringComparison.Ordinal))
            {
                normalized += "/*";
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var fixedSegments = segments.TakeWhile(segment => !ContainsWildcard(segment)).ToArray();
            var anchor = Anchor(baseDirectory, fixedSegments).Replace('\\', '/').TrimEnd('/');
            var builder = new StringBuilder("^").Append(Regex.Escape(anchor));
            foreach (var segment in segments.Skip(fixedSegments.Length))
            {
                builder.Append(segment == "**" ? "(?:/[^/]+)*" : "/" + TranslateSegment(segment));
            }

            var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
            if (OperatingSystem.IsWindows())
            {
                options |= RegexOptions.IgnoreCase;
            }

            return new GlobMatcher(new Regex(builder.Append('$').ToString(), options));
        }

        public bool IsMatch(string absolutePath) => pattern.IsMatch(absolutePath.Replace('\\', '/'));

        private static string TranslateSegment(string segment)
        {
            var builder = new StringBuilder();
            foreach (var character in segment)
            {
                builder.Append(character switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(character.ToString()),
                });
            }

            return builder.ToString();
        }
    }
}
