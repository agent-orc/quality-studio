using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Resolves the application-owned shadow tree used for Quality Studio state. The shadow tree keeps
/// repository-relative metadata paths stable without placing generated files in the checkout.
/// </summary>
public static class QualityDataRoot
{
    public const string EnvironmentVariable = "QUALITY_STUDIO_DATA_ROOT";

    public static string DefaultBasePath
    {
        get
        {
            var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData))
                applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(applicationData)) applicationData = Path.GetTempPath();
            return Path.Combine(applicationData, "QualityStudio", "projects");
        }
    }

    public static string ResolveBasePath(string? configuredPath = null, string? contentRoot = null)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : configuredPath;
        if (string.IsNullOrWhiteSpace(candidate)) return Path.GetFullPath(DefaultBasePath);
        return Path.GetFullPath(Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(contentRoot ?? Directory.GetCurrentDirectory(), candidate));
    }

    public static string ResolveProjectPath(string projectId, string? configuredBasePath = null,
        string? contentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (projectId is "." or ".." || projectId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            projectId.Contains(Path.DirectorySeparatorChar) || projectId.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Project id must be a single valid path segment.", nameof(projectId));
        return Path.Combine(ResolveBasePath(configuredBasePath, contentRoot), projectId);
    }

    public static string RepositoryProjectId(string registryId, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var identity = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        return registryId + "-" + suffix;
    }

    public static string MapCheckoutPath(string repositoryRoot, string projectDataRoot, string checkoutPath)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.GetFullPath(checkoutPath);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException("The metadata path is outside the repository root.", nameof(checkoutPath));
        return Path.GetFullPath(Path.Combine(projectDataRoot, relative));
    }
}

public sealed record QualityDataMigrationResult(
    int FilesMoved,
    int DirectoriesRemoved,
    bool AlreadyCompleted,
    int TrackedFilesCopied = 0);

/// <summary>Moves legacy in-checkout .quality trees to their matching shadow-tree locations once.</summary>
public static class QualityDataMigrator
{
    private const string MarkerName = ".migration-v1-complete";
    private static readonly EnumerationOptions ConfinedEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static QualityDataMigrationResult Migrate(string repositoryRoot, string projectDataRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var dataRoot = Path.GetFullPath(projectDataRoot);
        if (IsSameOrNested(root, dataRoot))
            throw new ArgumentException("The Quality Studio data root must be outside the repository checkout.", nameof(projectDataRoot));

        var marker = Path.Combine(dataRoot, MarkerName);
        if (File.Exists(marker)) return new(0, 0, true);

        var qualityDirectories = Directory.EnumerateDirectories(root, ".quality", ConfinedEnumeration)
            .Prepend(Path.Combine(root, ".quality"))
            .Where(Directory.Exists)
            .Where(path => !HasReparsePoint(root, path))
            .Distinct(PathComparer)
            .OrderByDescending(path => path.Length)
            .ToArray();
        var files = qualityDirectories.SelectMany(sourceDirectory =>
                Directory.EnumerateFiles(sourceDirectory, "*", ConfinedEnumeration)
                    .Where(path => !HasReparsePoint(sourceDirectory, path)))
            .Distinct(PathComparer)
            .Select(source => (Source: source,
                Destination: QualityDataRoot.MapCheckoutPath(root, dataRoot, source)))
            .ToArray();
        var tracked = GitTrackedQualityFiles(root);
        foreach (var file in files.Where(file => File.Exists(file.Destination)))
            if (!FilesEqual(file.Source, file.Destination))
                throw new IOException($"Migration destination already contains different data: {file.Destination}");

        var moved = 0;
        var copied = 0;
        var removed = 0;
        foreach (var (source, destination) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            if (tracked.Contains(relative))
            {
                if (!File.Exists(destination)) File.Copy(source, destination);
                copied++;
            }
            else
            {
                if (File.Exists(destination)) File.Delete(source); else File.Move(source, destination);
                moved++;
            }
        }

        foreach (var sourceDirectory in qualityDirectories)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", ConfinedEnumeration)
                         .OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (Directory.Exists(sourceDirectory) && !Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Directory.Delete(sourceDirectory);
                removed++;
            }
        }

        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
        return new(moved, removed, false, copied);
    }

    public static IReadOnlyList<string> DirtyQualityPaths(string repositoryRoot)
    {
        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")) && !File.Exists(Path.Combine(repositoryRoot, ".git")))
            return [];
        try
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
            if (!process.Start()) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Length > 3 ? line[3..] : line).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        var firstInfo = new FileInfo(first);
        var secondInfo = new FileInfo(second);
        if (firstInfo.Length != secondInfo.Length) return false;
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        Span<byte> firstBuffer = stackalloc byte[8192];
        Span<byte> secondBuffer = stackalloc byte[8192];
        while (true)
        {
            var firstRead = firstStream.Read(firstBuffer);
            var secondRead = secondStream.Read(secondBuffer);
            if (firstRead != secondRead || !firstBuffer[..firstRead].SequenceEqual(secondBuffer[..secondRead])) return false;
            if (firstRead == 0) return true;
        }
    }

    private static IReadOnlySet<string> GitTrackedQualityFiles(string repositoryRoot)
    {
        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")) && !File.Exists(Path.Combine(repositoryRoot, ".git")))
            return new HashSet<string>(StringComparer.Ordinal);
        try
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
            process.StartInfo.ArgumentList.Add("ls-files");
            process.StartInfo.ArgumentList.Add("-z");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(".quality");
            process.StartInfo.ArgumentList.Add(":(glob)**/.quality/**");
            if (!process.Start()) return new HashSet<string>(StringComparer.Ordinal);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in Path.GetRelativePath(current, Path.GetFullPath(path)).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }

    private static bool IsSameOrNested(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(prefix, candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               candidate.StartsWith(prefix + Path.DirectorySeparatorChar, comparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
