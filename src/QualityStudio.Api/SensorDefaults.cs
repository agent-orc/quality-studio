namespace QualityStudio.Api;

/// <summary>
/// Computes the default per-sensor enable flag and configuration for a repository based on
/// what is actually present on disk. A sensor that needs a repository-owned command (Roslyn,
/// tsc) is only default-enabled when that command can run; otherwise it stays disabled instead
/// of reporting a permanent "unavailable" result on every review.
/// </summary>
internal static class SensorDefaults
{
    private const string ReportDirectory = ".quality/analyzers";
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] TypeScriptProjectFiles = ["tsconfig.app.json", "tsconfig.json"];
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".angular", "dist", "out-tsc",
    };

    public static RepositorySensorConfiguration Configure(string id, string repositoryRoot) => id switch
    {
        "roslyn" => ConfigureRoslyn(repositoryRoot),
        "tsc" => ConfigureTypeScript(repositoryRoot),
        "eslint" or "sarif" => new RepositorySensorConfiguration(id, Enabled: false),
        _ => new RepositorySensorConfiguration(id),
    };

    private static RepositorySensorConfiguration ConfigureRoslyn(string repositoryRoot)
    {
        if (!HasDotnetSolution(repositoryRoot)) return new RepositorySensorConfiguration("roslyn", Enabled: false);

        return new RepositorySensorConfiguration("roslyn", Enabled: true,
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "dotnet build --nologo -clp:NoSummary /p:ErrorLog={reportPath}%2Cversion=2.1",
                ["reportPath"] = $"{ReportDirectory}/roslyn.sarif",
            });
    }

    private static RepositorySensorConfiguration ConfigureTypeScript(string repositoryRoot)
    {
        var project = FindTypeScriptProject(repositoryRoot);
        if (project is null) return new RepositorySensorConfiguration("tsc", Enabled: false);

        var (directory, configFile) = project.Value;
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["command"] = $"npx --no-install tsc -p {configFile} --noEmit",
            ["reportPath"] = $"{ReportDirectory}/tsc.txt",
        };
        var relativeDirectory = Path.GetRelativePath(repositoryRoot, directory).Replace('\\', '/');
        if (relativeDirectory != ".") configuration["workingDirectory"] = relativeDirectory;
        return new RepositorySensorConfiguration("tsc", Enabled: true, Configuration: configuration);
    }

    private static bool HasDotnetSolution(string repositoryRoot)
    {
        try
        {
            return Directory.EnumerateFiles(repositoryRoot)
                .Any(path => SolutionExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static (string Directory, string ConfigFile)? FindTypeScriptProject(string repositoryRoot)
    {
        foreach (var candidate in CandidateDirectories(repositoryRoot))
        foreach (var configFile in TypeScriptProjectFiles)
        {
            if (File.Exists(Path.Combine(candidate, configFile))) return (candidate, configFile);
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string repositoryRoot)
    {
        yield return repositoryRoot;
        IEnumerable<string> subdirectories;
        try
        {
            subdirectories = Directory.EnumerateDirectories(repositoryRoot)
                .Where(directory => !ExcludedDirectoryNames.Contains(Path.GetFileName(directory)))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var directory in subdirectories) yield return directory;
    }
}
