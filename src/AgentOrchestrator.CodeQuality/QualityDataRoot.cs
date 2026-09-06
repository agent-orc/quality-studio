using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Resolves and migrates Quality Studio's mutable, per-project state outside the checkout.</summary>
public static class QualityDataRoot
{
    public const string EnvironmentVariable = "QUALITY_STUDIO_DATA_ROOT";

    public static string ResolveProjectsRoot(string? configuredRoot = null)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredRoot)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : configuredRoot;
        if (!string.IsNullOrWhiteSpace(candidate)) return Path.GetFullPath(candidate);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.Combine(Path.GetTempPath(), "QualityStudio-local-data");
        return Path.Combine(local, "QualityStudio", "projects");
    }

    public static string ResolveProject(string projectId, string? configuredRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Project id may contain only letters, numbers, hyphens, and underscores.", nameof(projectId));
        return Path.Combine(ResolveProjectsRoot(configuredRoot), projectId.ToLowerInvariant());
    }

    public static string RepositoryIdentity(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var identity = GitValue(root, "config", "--get", "remote.origin.url") ?? root;
        var name = new DirectoryInfo(root).Name.ToLowerInvariant();
        var slug = new string(name.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "project";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        return $"{slug}-{hash}";
    }

    private static string? GitValue(string root, params string[] arguments)
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
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

public sealed record QualityDataMigrationResult(bool Performed, int FilesMoved, string DataRoot);

public static class QualityDataMigration
{
    private const string MarkerName = ".in-tree-quality-migration-v1.json";

    public static QualityDataMigrationResult Migrate(string repositoryRoot, string dataRoot)
    {
        var sourceRoot = Path.GetFullPath(repositoryRoot);
        var targetRoot = Path.GetFullPath(dataRoot);
        if (IsWithin(sourceRoot, targetRoot))
            throw new ArgumentException("Quality Studio data root must be outside the analysed checkout.", nameof(dataRoot));
        Directory.CreateDirectory(targetRoot);
        var marker = Path.Combine(targetRoot, MarkerName);
        if (File.Exists(marker)) return new QualityDataMigrationResult(false, 0, targetRoot);

        var qualityDirectories = Directory.EnumerateDirectories(sourceRoot, ".quality", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            .OrderByDescending(path => path.Length)
            .ToArray();
        var moved = 0;
        foreach (var qualityDirectory in qualityDirectories)
        {
            var owner = Path.GetDirectoryName(qualityDirectory)!;
            var ownerRelative = Path.GetRelativePath(sourceRoot, owner).Replace('\\', '/');
            foreach (var source in Directory.EnumerateFiles(qualityDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(qualityDirectory, source);
                var destination = ownerRelative == "."
                    ? Path.Combine(targetRoot, ".quality", relative)
                    : Path.Combine(targetRoot, ".quality", "by-path", ownerRelative.Replace('/', Path.DirectorySeparatorChar), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination)))
                        throw new IOException($"Migration destination already contains different data: {destination}");
                    File.Delete(source);
                }
                else
                {
                    File.Move(source, destination);
                }
                moved++;
            }
            DeleteEmptyDirectories(qualityDirectory);
        }

        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            repositoryIdentity = QualityDataRoot.RepositoryIdentity(sourceRoot),
            migratedAt = DateTimeOffset.UtcNow,
            filesMoved = moved,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
        return new QualityDataMigrationResult(true, moved, targetRoot);
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root).ToArray()) DeleteEmptyDirectories(directory);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(root, path, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
