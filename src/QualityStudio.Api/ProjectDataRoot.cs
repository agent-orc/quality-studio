using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QualityStudio.Api;

public sealed record ProjectDataMigrationResult(int CopiedFiles, int MovedFiles, int PreservedTrackedFiles, int Conflicts);

/// <summary>Resolves and initializes mutable storage for one registered repository.</summary>
public sealed class ProjectDataRoot
{
    public const string MigrationMarker = ".in-tree-quality-migration-v1.json";
    private readonly string basePath;

    public ProjectDataRoot(IHostEnvironment environment, Microsoft.Extensions.Options.IOptions<RepositoryOptions> options)
    {
        var configured = options.Value.DataRoot;
        basePath = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QualityStudio", "projects")
            : Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured));
    }

    public string Resolve(string projectId, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Project id is not safe for a data-root directory.", nameof(projectId));
        var path = Path.GetFullPath(Path.Combine(basePath, ProjectDirectoryName(projectId, repositoryRoot)));
        if (PathConfinement.IsWithin(Path.GetFullPath(repositoryRoot), path))
            throw new InvalidOperationException("QualityStudio:DataRoot must be outside every analysed repository.");
        return path;
    }

    public static string ProjectDirectoryName(string projectId, string repositoryRoot)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
        return $"{projectId.ToLowerInvariant()}-{hash}";
    }

    public string RegistryPath => Path.Combine(basePath, "repositories.json");

    public ProjectDataMigrationResult MigrateOnce(string repositoryRoot, string dataRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        dataRoot = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(dataRoot);
        var marker = Path.Combine(dataRoot, MigrationMarker);
        if (File.Exists(marker)) return new ProjectDataMigrationResult(0, 0, 0, 0);

        var tracked = GitTrackedQualityFiles(repositoryRoot);
        var copied = 0;
        var moved = 0;
        var preserved = 0;
        var conflicts = 0;
        foreach (var qualityDirectory in EnumerateQualityDirectories(repositoryRoot))
        {
            foreach (var source in Directory.EnumerateFiles(qualityDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repositoryRoot, source);
                var destination = Path.Combine(dataRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!FilesEqual(source, destination))
                    {
                        conflicts++;
                        continue;
                    }
                }
                else
                {
                    File.Copy(source, destination);
                    copied++;
                }

                if (tracked.Contains(relative.Replace('\\', '/')))
                {
                    preserved++;
                }
                else
                {
                    File.Delete(source);
                    moved++;
                }
            }
        }

        var result = new ProjectDataMigrationResult(copied, moved, preserved, conflicts);
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            repositoryIdentity = Path.GetFileName(dataRoot),
            migratedAt = DateTimeOffset.UtcNow,
            filesMoved = result.MovedFiles,
            filesCopied = result.CopiedFiles,
            preservedTrackedFiles = result.PreservedTrackedFiles,
            conflicts = result.Conflicts,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
        RemoveEmptyQualityDirectories(repositoryRoot);
        return result;
    }

    public static bool HasDirtyQualityTree(string repositoryRoot, out string summary)
    {
        var result = RunGit(repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all", "--",
            ":(glob).quality/**", ":(glob)**/.quality/**");
        summary = result.Output.Trim();
        return result.ExitCode == 0 && summary.Length > 0;
    }

    private static IEnumerable<string> EnumerateQualityDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name == ".quality")
                {
                    yield return child;
                    continue;
                }
                if (name is ".git" or "node_modules" or "bin" or "obj") continue;
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
            }
        }
    }

    private static HashSet<string> GitTrackedQualityFiles(string root)
    {
        var result = RunGit(root, "ls-files", "-z", "--", ":(glob).quality/**", ":(glob)**/.quality/**");
        return result.ExitCode == 0
            ? result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    private static (int ExitCode, string Output) RunGit(string root, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    private static void RemoveEmptyQualityDirectories(string root)
    {
        foreach (var directory in EnumerateQualityDirectories(root).OrderByDescending(path => path.Length))
        {
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
