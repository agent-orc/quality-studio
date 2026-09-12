using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public static partial class RepositoryHierarchyBuilder
{
    private static readonly HashSet<string> AngularSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".html", ".css", ".scss", ".sass", ".less",
    };

    /// <summary>
    /// Selects hierarchy adapters from repository content. Recognized adapters may coexist;
    /// the path-based generic adapter is used only when no recognized workspace is present.
    /// </summary>
    public static IReadOnlyList<HierarchyNode> Build(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.GetFullPath(repositoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        }

        var files = EnumerateRepositoryFiles(root);
        var result = new List<HierarchyNode>();
        if (files.Any(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            result.AddRange(BuildDotNet(root));
        }

        var angular = BuildAngular(root, files);
        result.AddRange(angular);
        if (result.Count == 0)
        {
            result.Add(BuildGeneric(root, files));
        }

        return result;
    }

    public static IReadOnlyList<HierarchyNode> BuildAngular(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.GetFullPath(repositoryPath);
        return BuildAngular(root, EnumerateRepositoryFiles(root));
    }

    public static IReadOnlyList<HierarchyNode> BuildGeneric(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.GetFullPath(repositoryPath);
        return [BuildGeneric(root, EnumerateRepositoryFiles(root))];
    }

    private static IReadOnlyList<HierarchyNode> BuildAngular(string root, IReadOnlyList<string> repositoryFiles)
    {
        var definitions = DiscoverAngularProjects(root, repositoryFiles);
        return definitions.Select(definition => BuildAngularProject(root, repositoryFiles, definition)).ToArray();
    }

    private static IReadOnlyList<AngularProjectDefinition> DiscoverAngularProjects(
        string root, IReadOnlyList<string> repositoryFiles)
    {
        var definitions = new List<AngularProjectDefinition>();
        var angularFiles = repositoryFiles
            .Where(path => Path.GetFileName(path).Equals("angular.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var angularPath in angularFiles)
        {
            using var document = ParseJson(Path.Combine(root, Native(angularPath)));
            if (!document.RootElement.TryGetProperty("projects", out var projects) ||
                projects.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var workspaceDirectory = RepositoryDirectory(angularPath);
            foreach (var property in projects.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var configuredRoot = property.Value.TryGetProperty("root", out var rootProperty)
                    ? rootProperty.GetString()
                    : null;
                var projectRoot = CombineRepositoryPath(workspaceDirectory, configuredRoot);
                var configuredSourceRoot = property.Value.TryGetProperty("sourceRoot", out var sourceRootProperty)
                    ? sourceRootProperty.GetString()
                    : null;
                var sourceRoot = string.IsNullOrWhiteSpace(configuredSourceRoot)
                    ? projectRoot
                    : CombineRepositoryPath(workspaceDirectory, configuredSourceRoot);
                definitions.Add(new AngularProjectDefinition(angularPath, property.Name, projectRoot, sourceRoot));
            }
        }

        // An angular.json is authoritative for its workspace. The two additional discovery
        // forms cover TypeScript monorepos which deliberately do not use Angular CLI metadata.
        if (definitions.Count > 0)
        {
            return definitions;
        }

        foreach (var packagePath in repositoryFiles
                     .Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            using var package = TryParseJson(Path.Combine(root, Native(packagePath)));
            if (package is null || !package.RootElement.TryGetProperty("workspaces", out var workspaces))
            {
                continue;
            }

            var patterns = ReadWorkspacePatterns(workspaces);
            var workspaceDirectory = RepositoryDirectory(packagePath);
            foreach (var candidatePackage in repositoryFiles
                         .Where(path => Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase) &&
                                        !StringComparer.Ordinal.Equals(path, packagePath))
                         .Order(StringComparer.Ordinal))
            {
                var candidateDirectory = RepositoryDirectory(candidatePackage);
                var relativeDirectory = RelativeRepositoryPath(workspaceDirectory, candidateDirectory);
                if (!patterns.Any(pattern => WorkspacePatternMatches(pattern, relativeDirectory)))
                {
                    continue;
                }

                using var candidate = TryParseJson(Path.Combine(root, Native(candidatePackage)));
                var name = candidate is not null && candidate.RootElement.TryGetProperty("name", out var nameProperty)
                    ? nameProperty.GetString()
                    : null;
                definitions.Add(new AngularProjectDefinition(packagePath,
                    string.IsNullOrWhiteSpace(name) ? Path.GetFileName(candidateDirectory) : name!,
                    candidateDirectory, candidateDirectory));
            }
        }

        if (definitions.Count > 0)
        {
            return definitions.DistinctBy(definition => (definition.ConfigPath, definition.Name)).ToArray();
        }

        foreach (var configPath in repositoryFiles
                     .Where(path => Path.GetFileName(path).StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) &&
                                    path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            using var config = TryParseJson(Path.Combine(root, Native(configPath)));
            if (config is null || !config.RootElement.TryGetProperty("references", out var references) ||
                references.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var reference in references.EnumerateArray())
            {
                if (!reference.TryGetProperty("path", out var pathProperty) ||
                    string.IsNullOrWhiteSpace(pathProperty.GetString()))
                {
                    continue;
                }

                var referenced = CombineRepositoryPath(RepositoryDirectory(configPath), pathProperty.GetString());
                var projectRoot = referenced.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? RepositoryDirectory(referenced)
                    : referenced;
                var name = projectRoot == "." ? Path.GetFileName(root) : Path.GetFileName(projectRoot);
                definitions.Add(new AngularProjectDefinition(configPath, name, projectRoot, projectRoot));
            }
        }

        return definitions.DistinctBy(definition => (definition.ConfigPath, definition.Name)).ToArray();
    }

    private static HierarchyNode BuildAngularProject(
        string root, IReadOnlyList<string> repositoryFiles, AngularProjectDefinition definition)
    {
        var project = new HierarchyNode(
            AdapterId("angular", ReviewLevel.Project, [definition.ConfigPath, definition.Name]),
            definition.Name,
            ReviewLevel.Project,
            definition.ConfigPath);
        var module = new HierarchyNode(
            AdapterId("angular", ReviewLevel.Module, [project.Id, "root", definition.ProjectRoot, "root"]),
            definition.Name,
            ReviewLevel.Module,
            definition.ProjectRoot);
        project.AddChild(module);

        var sources = repositoryFiles
            .Where(path => IsWithinRepositoryDirectory(path, definition.SourceRoot))
            .Where(path => AngularSourceExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !IsAngularTestOrGenerated(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var group in sources.GroupBy(RepositoryDirectory).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var directory = group.Key;
            var ns = new HierarchyNode(
                AdapterId("angular", ReviewLevel.Namespace, [module.Id, directory]),
                directory,
                ReviewLevel.Namespace,
                directory);
            module.AddChild(ns);
            foreach (var relativePath in group)
            {
                ns.AddChild(BuildPathFile(root, "angular", module.Id, relativePath));
            }
        }

        return project;
    }

    private static HierarchyNode BuildGeneric(string root, IReadOnlyList<string> repositoryFiles)
    {
        var project = new HierarchyNode(
            AdapterId("generic", ReviewLevel.Project, [".", "synthetic-generic-project"]),
            Path.GetFileName(root),
            ReviewLevel.Project,
            ".");
        var module = new HierarchyNode(
            AdapterId("generic", ReviewLevel.Module, [project.Id, "root", "."]),
            Path.GetFileName(root),
            ReviewLevel.Module,
            ".");
        project.AddChild(module);

        foreach (var group in repositoryFiles
                     .Where(path => !IsReviewInfrastructure(path))
                     .Order(StringComparer.Ordinal)
                     .GroupBy(RepositoryDirectory)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var directory = group.Key;
            var ns = new HierarchyNode(
                AdapterId("generic", ReviewLevel.Namespace, [module.Id, directory]),
                directory,
                ReviewLevel.Namespace,
                directory);
            module.AddChild(ns);
            foreach (var relativePath in group)
            {
                ns.AddChild(BuildPathFile(root, "generic", module.Id, relativePath));
            }
        }

        return project;
    }

    private static HierarchyNode BuildPathFile(string root, string adapter, string moduleId, string relativePath)
    {
        var absolutePath = Path.Combine(root, Native(relativePath));
        var info = new FileInfo(absolutePath);
        var lineCount = CountLines(absolutePath, info.Length);
        return new HierarchyNode(
            AdapterId(adapter, ReviewLevel.File, [moduleId, relativePath]),
            Path.GetFileName(relativePath),
            ReviewLevel.File,
            relativePath,
            info.Length,
            lineCount);
    }

    private static IReadOnlyList<string> EnumerateRepositoryFiles(string root)
    {
        var gitFiles = TryEnumerateGitFiles(root);
        if (gitFiles is not null)
        {
            return gitFiles.Where(path => !IsBuildOutput(root, Path.Combine(root, Native(path))) &&
                                          !IsPackageOutput(path))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }

        var fallbackFiles = Directory.EnumerateFiles(root, "*", ConfinedEnumeration)
            .Where(path => !IsBuildOutput(root, path))
            .Select(path => Relative(root, path))
            .Where(path => !IsPackageOutput(path))
            .ToArray();
        var ignoreRules = ReadGitIgnoreRules(root, fallbackFiles);
        return fallbackFiles
            .Where(path => !IsIgnored(path, ignoreRules))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string>? TryEnumerateGitFiles(string root)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("ls-files");
        process.StartInfo.ArgumentList.Add("--cached");
        process.StartInfo.ArgumentList.Add("--others");
        process.StartInfo.ArgumentList.Add("--exclude-standard");
        process.StartInfo.ArgumentList.Add("-z");
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Replace('\\', '/')).ToArray()
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static int CountLines(string path, long length)
    {
        if (length == 0) return 0;
        var lines = 1;
        using var stream = File.OpenRead(path);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n') lines++;
            }
        }
        return lines;
    }

    private static string AdapterId(string adapter, ReviewLevel level, string[] tuple)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tuple);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return $"qs-v1/{adapter}/{level.ToString().ToLowerInvariant()}/{hash}";
    }

    private static JsonDocument ParseJson(string path) => JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    private static JsonDocument? TryParseJson(string path)
    {
        try { return ParseJson(path); }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> ReadWorkspacePatterns(JsonElement workspaces)
    {
        var source = workspaces.ValueKind == JsonValueKind.Array
            ? workspaces
            : workspaces.ValueKind == JsonValueKind.Object && workspaces.TryGetProperty("packages", out var packages)
                ? packages
                : default;
        return source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Replace('\\', '/').Trim('/')).ToArray()
            : [];
    }

    private static bool WorkspacePatternMatches(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static IReadOnlyList<GitIgnoreRule> ReadGitIgnoreRules(string root, IEnumerable<string> files)
    {
        var rules = new List<GitIgnoreRule>();
        foreach (var ignorePath in files.Where(path => Path.GetFileName(path) == ".gitignore")
                     .Order(StringComparer.Ordinal))
        {
            var baseDirectory = RepositoryDirectory(ignorePath);
            foreach (var rawLine in File.ReadLines(Path.Combine(root, Native(ignorePath))))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var negated = line[0] == '!';
                if (negated) line = line[1..];
                if (line.Length == 0) continue;
                rules.Add(new GitIgnoreRule(baseDirectory, GitIgnoreRegex(line), negated));
            }
        }
        return rules;
    }

    private static bool IsIgnored(string path, IReadOnlyList<GitIgnoreRule> rules)
    {
        var ignored = false;
        foreach (var rule in rules)
        {
            if (rule.BaseDirectory != "." && !path.StartsWith(rule.BaseDirectory + "/", StringComparison.Ordinal))
            {
                continue;
            }
            var scopedPath = rule.BaseDirectory == "." ? path : path[(rule.BaseDirectory.Length + 1)..];
            if (rule.Pattern.IsMatch(scopedPath)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private static Regex GitIgnoreRegex(string pattern)
    {
        pattern = pattern.TrimEnd('/');
        var anchored = pattern.StartsWith('/');
        pattern = pattern.TrimStart('/');
        var containsSlash = pattern.Contains('/');
        var builder = new StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    builder.Append(".*");
                    index++;
                }
                else builder.Append("[^/]*");
            }
            else if (pattern[index] == '?') builder.Append("[^/]");
            else builder.Append(Regex.Escape(pattern[index].ToString()));
        }
        var prefix = anchored || containsSlash ? "^" : "(?:^|.*/)";
        return new Regex(prefix + builder + "(?:/.*)?$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static string CombineRepositoryPath(string directory, string? child)
    {
        if (string.IsNullOrWhiteSpace(child)) return directory;
        var combined = directory == "." ? child! : directory + "/" + child;
        var segments = new List<string>();
        foreach (var segment in combined.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == ".." && segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            else if (segment != "..") segments.Add(segment);
        }
        return segments.Count == 0 ? "." : string.Join('/', segments);
    }

    private static string RelativeRepositoryPath(string directory, string path)
    {
        if (directory == ".") return path;
        return path.StartsWith(directory + "/", StringComparison.Ordinal) ? path[(directory.Length + 1)..] : path;
    }

    private static string RepositoryDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }

    private static bool IsWithinRepositoryDirectory(string path, string directory) =>
        directory == "." || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static bool IsAngularTestOrGenerated(string path) =>
        path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".ngtypecheck.ts", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".generated.ts", StringComparison.OrdinalIgnoreCase);

    private static bool IsPackageOutput(string path) => path.Split('/').Any(part =>
        part is "node_modules" or "dist" or "out" or "coverage" or ".angular" or "out-tsc");

    private static bool IsReviewInfrastructure(string path) =>
        path.Split('/').Any(part => part is ".quality" or ".git") ||
        path.Contains(".review-meta.", StringComparison.Ordinal);

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private sealed record AngularProjectDefinition(string ConfigPath, string Name, string ProjectRoot, string SourceRoot);
    private sealed record GitIgnoreRule(string BaseDirectory, Regex Pattern, bool Negated);
}
