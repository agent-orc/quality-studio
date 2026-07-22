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
        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (solutions.Length == 0)
        {
            return [BuildProject(root, null, FindProjectFiles(root))];
        }

        return solutions.Select(solution =>
                BuildProject(root, solution, FindSolutionProjects(root, solution)))
            .ToArray();
    }

    private static HierarchyNode BuildProject(string root, string? solution, IEnumerable<string> projects)
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

            project.AddChild(BuildModule(root, project, projectFile));
        }

        return project;
    }

    private static HierarchyNode BuildModule(string root, HierarchyNode project, string projectFile)
    {
        var relativeProject = Relative(root, projectFile);
        var module = new HierarchyNode(
            Id(ReviewLevel.Module, [project.Id, relativeProject]),
            Path.GetFileNameWithoutExtension(projectFile),
            ReviewLevel.Module,
            relativeProject);
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var files = Directory.EnumerateFiles(projectDirectory, "*.cs", ConfinedEnumeration)
            .Where(path => !IsBuildOutput(projectDirectory, path))
            .Order(StringComparer.Ordinal);

        foreach (var group in files.GroupBy(ReadNamespace).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ns = new HierarchyNode(
                Id(ReviewLevel.Namespace, [module.Id, group.Key]),
                group.Key,
                ReviewLevel.Namespace,
                $"{relativeProject}/.namespaces/{Uri.EscapeDataString(group.Key)}");
            module.AddChild(ns);

            foreach (var sourcePath in group)
            {
                var relativeSource = Relative(root, sourcePath);
                var sourceContent = File.ReadAllText(sourcePath);
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
            return XDocument.Load(solution).Descendants("Project")
                .Select(element => element.Attribute("Path")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!, Path.GetDirectoryName(solution)!));
        }

        return File.ReadLines(solution)
            .Select(line => SlnProjectRegex().Match(line))
            .Where(match => match.Success)
            .Select(match => Path.GetFullPath(match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar), Path.GetDirectoryName(solution)!))
            .Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> FindProjectFiles(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", ConfinedEnumeration)
            .Where(path => !IsBuildOutput(root, path));

    private static bool IsBuildOutput(string basePath, string path)
    {
        var relative = Path.GetRelativePath(basePath, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or ".quality" or ".git");
    }

    private static string ReadNamespace(string path)
    {
        var match = NamespaceRegex().Match(File.ReadAllText(path));
        return match.Success ? match.Groups[1].Value : "<global>";
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

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
