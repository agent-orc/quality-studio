using System.Security.Cryptography;
using System.Text.Json;

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

    /// <summary>
    /// Derives the .NET hierarchy. Solutions and project files are parsed structurally and C#
    /// sources are parsed with the Roslyn C# parser; see <c>docs/hierarchy-derivation.md</c> for
    /// what this derivation guarantees and where it stops.
    /// </summary>
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

        // Every project in the repository takes part in the nearest-project-ancestor rule, even one
        // that no solution references; otherwise an outer project would swallow its sources.
        var repositoryProjects = DotNetProjectItems.DiscoverProjectFiles(root);
        if (solutions.Length == 0)
        {
            return [BuildProject(root, scope, null, repositoryProjects, repositoryProjects)];
        }

        return solutions.Select(solution => BuildProject(
                root,
                scope,
                solution,
                DotNetProjectItems.ReadSolutionProjects(solution).Where(path => IsContained(root, path)).ToArray(),
                repositoryProjects))
            .ToArray();
    }

    private static HierarchyNode BuildProject(
        string root,
        RepositoryScope scope,
        string? solution,
        IEnumerable<string> projects,
        IReadOnlyCollection<string> repositoryProjects)
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

            var module = BuildModule(root, scope, project, projectFile, repositoryProjects);
            project.AddChild(module);
            project.AddExclusions(module.Exclusions);
        }

        return project;
    }

    private static HierarchyNode BuildModule(
        string root,
        RepositoryScope scope,
        HierarchyNode project,
        string projectFile,
        IReadOnlyCollection<string> repositoryProjects)
    {
        var relativeProject = Relative(root, projectFile);
        var module = new HierarchyNode(
            Id(ReviewLevel.Module, [project.Id, relativeProject]),
            Path.GetFileNameWithoutExtension(projectFile),
            ReviewLevel.Module,
            relativeProject);
        var candidates = DotNetProjectItems.ResolveCompileItems(root, projectFile, repositoryProjects)
            .Where(path => IsContained(root, path))
            .Select(path => (Path: path, Relative: Relative(root, path)))
            .OrderBy(candidate => candidate.Relative, StringComparer.Ordinal)
            .Select(candidate => (
                candidate.Path,
                candidate.Relative,
                Decision: scope.Evaluate(candidate.Relative, candidate.Path)))
            .ToArray();
        foreach (var candidate in candidates.Where(candidate => !candidate.Decision.Included))
        {
            module.AddExclusion(new ScopeExclusion(candidate.Relative, candidate.Decision.Reason!));
        }

        var sources = candidates.Where(candidate => candidate.Decision.Included)
            .Select(candidate => BuildSourceFile(module, candidate.Path, candidate.Relative))
            .ToArray();

        foreach (var namespaceName in sources.SelectMany(source => source.Namespaces)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var ns = new HierarchyNode(
                Id(ReviewLevel.Namespace, [module.Id, namespaceName]),
                namespaceName,
                ReviewLevel.Namespace,
                $"{relativeProject}/.namespaces/{Uri.EscapeDataString(namespaceName)}");
            module.AddChild(ns);

            // A source contributing to several namespaces is aliased below each of them; the
            // contract keeps one canonical File unit, so the same node instance is reused.
            foreach (var source in sources.Where(source =>
                         source.Namespaces.Contains(namespaceName, StringComparer.Ordinal)))
            {
                ns.AddChild(source.Node);
            }
        }

        return module;
    }

    private static DerivedSource BuildSourceFile(HierarchyNode module, string absolutePath, string relativePath)
    {
        var content = File.ReadAllText(absolutePath);
        var units = CSharpSyntaxUnits.Parse(content);
        var node = new HierarchyNode(
            Id(ReviewLevel.File, [module.Id, relativePath]),
            Path.GetFileName(absolutePath),
            ReviewLevel.File,
            relativePath,
            new FileInfo(absolutePath).Length,
            content.Length == 0 ? 0 : content.Count(character => character == '\n') + 1);

        foreach (var function in units.Functions)
        {
            node.AddChild(new HierarchyNode(
                Id(ReviewLevel.Function, [node.Id, CSharpSyntaxUnits.FunctionKey, function.DocumentationId]),
                function.Name,
                ReviewLevel.Function,
                relativePath));
        }

        return new DerivedSource(units.Namespaces, node);
    }

    private static bool IsBuildOutput(string basePath, string path)
    {
        var relative = Path.GetRelativePath(basePath, path);
        return relative.Split(Path.DirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or ".quality" or ".git");
    }

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

    private sealed record DerivedSource(IReadOnlyList<string> Namespaces, HierarchyNode Node);
}
