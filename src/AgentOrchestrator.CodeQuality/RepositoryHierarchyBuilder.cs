using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Derives five-level repository hierarchies through content-selected adapters.
/// The .NET entry point remains public so its canonical IDs and existing callers stay stable.
/// </summary>
public static partial class RepositoryHierarchyBuilder
{
    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static IReadOnlyList<HierarchyNode> BuildDotNet(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.GetFullPath(repositoryPath);
        var scope = RepositoryScope.Load(root);
        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (solutions.Length == 0)
        {
            return [BuildProject(root, scope, null, FindProjectFiles(root))];
        }

        return solutions.Select(solution =>
                BuildProject(root, scope, solution, FindSolutionProjects(root, solution)))
            .ToArray();
    }

    private static HierarchyNode BuildProject(string root, RepositoryScope scope, string? solution, IEnumerable<string> projects)
    {
        var projectPath = solution is null ? "." : Relative(root, solution);
        var projectTuple = solution is null
            ? new[] { ".", "synthetic-dotnet-project" }
            : new[] { projectPath };
        var project = new HierarchyNode(
            Id(ReviewLevel.Project, projectTuple),
            solution is null ? Path.GetFileName(root) : Path.GetFileNameWithoutExtension(solution),
            ReviewLevel.Project,
            projectPath);

        foreach (var projectFile in projects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(projectFile))
            {
                continue;
            }

            var relativeProjectFile = Relative(root, projectFile);
            var decision = scope.Evaluate(relativeProjectFile, projectFile);
            if (!decision.Included)
            {
                project.AddExclusion(new ScopeExclusion(relativeProjectFile, decision.Reason!));
                continue;
            }

            var module = BuildModule(root, scope, project, projectFile);
            project.AddChild(module);
            project.AddExclusions(module.Exclusions);
        }

        return project;
    }

    private static HierarchyNode BuildModule(string root, RepositoryScope scope, HierarchyNode project, string projectFile)
    {
        var relativeProject = Relative(root, projectFile);
        var module = new HierarchyNode(
            Id(ReviewLevel.Module, [project.Id, relativeProject]),
            Path.GetFileNameWithoutExtension(projectFile),
            ReviewLevel.Module,
            relativeProject);
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var candidates = Directory.EnumerateFiles(projectDirectory, "*.cs", ConfinedEnumeration)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Relative: Relative(root, path), Decision: scope.Evaluate(Relative(root, path), path)))
            .ToArray();
        foreach (var candidate in candidates.Where(candidate => !candidate.Decision.Included))
        {
            module.AddExclusion(new ScopeExclusion(candidate.Relative, candidate.Decision.Reason!));
        }
        var files = candidates.Where(candidate => candidate.Decision.Included)
            .Select(candidate => new SourceFile(candidate.Path, BoundedRepositoryFile.ReadEnumeratedText(
                root, candidate.Path, ReviewContentLimits.Default.MaxFileBytes)))
            .ToArray();

        foreach (var group in files.GroupBy(source => ReadNamespace(source.Content)).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ns = new HierarchyNode(
                Id(ReviewLevel.Namespace, [module.Id, group.Key]),
                group.Key,
                ReviewLevel.Namespace,
                $"{relativeProject}/.namespaces/{Uri.EscapeDataString(group.Key)}");
            module.AddChild(ns);

            foreach (var source in group)
            {
                var sourcePath = source.Path;
                var relativeSource = Relative(root, sourcePath);
                var sourceContent = source.Content;
                var file = new HierarchyNode(
                    Id(ReviewLevel.File, [module.Id, relativeSource]),
                    Path.GetFileName(sourcePath),
                    ReviewLevel.File,
                    relativeSource,
                    new FileInfo(sourcePath).Length,
                    sourceContent.Length == 0 ? 0 : sourceContent.Count(character => character == '\n') + 1);
                ns.AddChild(file);

                var symbols = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match match in FunctionRegex().Matches(sourceContent))
                {
                    var symbol = $"{group.Key}.{match.Groups[1].Value}";
                    if (!symbols.Add(symbol))
                    {
                        continue;
                    }

                    file.AddChild(new HierarchyNode(
                        Id(ReviewLevel.Function, [file.Id, "csharp-source-key-v1", symbol]),
                        match.Groups[1].Value,
                        ReviewLevel.Function,
                        relativeSource));
                }
            }
        }

        return module;
    }

    private static IEnumerable<string> FindSolutionProjects(string root, string solution)
    {
        if (Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return XDocument.Parse(BoundedRepositoryFile.ReadAllText(
                    root, solution, ReviewContentLimits.Default.MaxFileBytes)).Descendants("Project")
                .Select(element => element.Attribute("Path")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!, Path.GetDirectoryName(solution)!))
                .Where(path => IsContained(root, path));
        }

        return BoundedRepositoryFile.ReadAllText(root, solution, ReviewContentLimits.Default.MaxFileBytes)
            .Split('\n')
            .Select(line => SlnProjectRegex().Match(line))
            .Where(match => match.Success)
            .Select(match => Path.GetFullPath(match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(solution)!))
            .Where(path => IsContained(root, path));
    }

    private static IEnumerable<string> FindProjectFiles(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", ConfinedEnumeration);

    private static bool IsBuildOutput(string basePath, string path)
    {
        var relative = Path.GetRelativePath(basePath, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or ".quality" or ".git");
    }

    private static string ReadNamespace(string content)
    {
        var match = NamespaceRegex().Match(content);
        return match.Success ? match.Groups[1].Value : "<global>";
    }

    private sealed record SourceFile(string Path, string Content);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsContained(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)) return false;
        var current = normalizedRoot;
        foreach (var segment in Path.GetRelativePath(normalizedRoot, normalizedPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static string Id(ReviewLevel level, string[] tuple)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tuple);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"qs-v1/dotnet/{level.ToString().ToLowerInvariant()}/{hash}";
    }

    [GeneratedRegex(@"^Project\([^)]*\) = ""[^""]*"", ""([^""]+\.csproj)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectRegex();

    [GeneratedRegex(@"\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"(?m)^\s*(?:public|private|protected|internal|static|virtual|override|abstract|async|sealed|new|extern|partial|unsafe|\s)+\s*[A-Za-z_][A-Za-z0-9_<>,.?\[\]\s]*\s+([A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*(?:=>|\{)")]
    private static partial Regex FunctionRegex();
}
