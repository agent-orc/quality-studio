using System.Diagnostics;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public static class ProjectDataRootResolver
{
    public static string ResolveBaseRoot(string? configuredRoot, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.GetFullPath(Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(contentRoot, configuredRoot));
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("The local application data directory could not be resolved. Configure QualityStudio:DataRoot explicitly.");
        return Path.Combine(local, "QualityStudio", "projects");
    }

    public static string ResolveProjectRoot(string baseRoot, string projectId) =>
        Path.Combine(Path.GetFullPath(baseRoot), projectId);
}

public sealed record ProjectDataMigrationResult(bool Migrated, int FilesMoved, string DataRoot);

public static class ProjectDataMigrator
{
    private const string MarkerName = ".migration-v1.json";

    public static ProjectDataMigrationResult Migrate(string repositoryRoot, string dataRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var targetRoot = Path.GetFullPath(dataRoot);
        var marker = Path.Combine(targetRoot, MarkerName);
        if (File.Exists(marker)) return new ProjectDataMigrationResult(false, 0, targetRoot);
        if (PathConfinement.IsWithin(root, targetRoot))
            throw new InvalidOperationException("QualityStudio:DataRoot must be outside every analyzed checkout.");

        var qualityDirectories = Directory.EnumerateDirectories(root, ".quality", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment == ".git"))
            .OrderBy(path => path.Count(character => character is '/' or '\\'))
            .ToArray();
        var moved = 0;
        foreach (var qualityDirectory in qualityDirectories)
        {
            var anchor = Path.GetRelativePath(root, Path.GetDirectoryName(qualityDirectory)!)
                .Replace('\\', '/');
            foreach (var source in Directory.EnumerateFiles(qualityDirectory, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var withinQuality = Path.GetRelativePath(qualityDirectory, source).Replace('\\', '/');
                var logical = anchor == "." || withinQuality.StartsWith("reviews/", StringComparison.Ordinal)
                    ? withinQuality
                    : $"legacy/{anchor}/{withinQuality}";
                var destination = Path.Combine(targetRoot, logical.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                        throw new IOException($"Migration destination already contains different data: {destination}");
                    File.Delete(source);
                }
                else
                {
                    File.Move(source, destination);
                }
                moved++;
            }
        }

        foreach (var directory in qualityDirectories.OrderByDescending(path => path.Length))
            DeleteEmptyTree(directory);
        Directory.CreateDirectory(targetRoot);
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            migratedAt = DateTimeOffset.UtcNow,
            repositoryRoot = root,
            filesMoved = moved,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
        return new ProjectDataMigrationResult(true, moved, targetRoot);
    }

    public static IReadOnlyList<string> DirtyQualityPaths(string repositoryRoot)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("status");
        process.StartInfo.ArgumentList.Add("--porcelain=v1");
        process.StartInfo.ArgumentList.Add("--untracked-files=all");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(".quality");
        process.StartInfo.ArgumentList.Add(":(glob)**/.quality/**");
        try
        {
            if (!process.Start()) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Length > 3 ? line[3..] : line).ToArray()
                : [];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static void DeleteEmptyTree(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var child in Directory.EnumerateDirectories(root).ToArray()) DeleteEmptyTree(child);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }
}
