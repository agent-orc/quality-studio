using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Resolves the writable Quality Studio data directory for a read-only repository checkout.
/// Repository paths remain the identity and subject boundary; runtime state is stored here.
/// </summary>
public static class QualityDataRoot
{
    public const string EnvironmentVariable = "QUALITY_STUDIO_DATA_ROOT";
    private static readonly ConcurrentDictionary<string, string> Registered = new(PathComparer);

    public static string DefaultBasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QualityStudio",
        "projects");

    public static string Register(string repositoryRoot, string projectId, string? configuredBasePath = null)
    {
        var root = RequireRepositoryRoot(repositoryRoot);
        var projectRoot = Path.Combine(ResolveBasePath(configuredBasePath), SafeProjectId(projectId));
        projectRoot = Path.GetFullPath(projectRoot);
        EnsureSeparate(root, projectRoot);
        Registered[root] = projectRoot;
        return projectRoot;
    }

    public static string Resolve(string repositoryRoot, string? explicitProjectRoot = null)
    {
        var root = RequireRepositoryRoot(repositoryRoot);
        if (!string.IsNullOrWhiteSpace(explicitProjectRoot))
        {
            var resolved = Path.GetFullPath(explicitProjectRoot);
            EnsureSeparate(root, resolved);
            return resolved;
        }

        if (Registered.TryGetValue(root, out var registered)) return registered;
        return Path.Combine(ResolveBasePath(null), RepositoryIdentity(root));
    }

    public static string PathFor(string repositoryRoot, string legacyRelativePath, string? explicitProjectRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRelativePath);
        var relative = legacyRelativePath.Replace('\\', '/').TrimStart('/');
        if (relative.Equals(".quality", StringComparison.Ordinal)) relative = string.Empty;
        else if (relative.StartsWith(".quality/", StringComparison.Ordinal)) relative = relative[9..];
        var dataRoot = Resolve(repositoryRoot, explicitProjectRoot);
        var result = Path.GetFullPath(Path.Combine(dataRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(dataRoot, result))
            throw new ArgumentException("Logical Quality Studio path escapes the project data root.",
                nameof(legacyRelativePath));
        return result;
    }

    public static string LogicalPath(string repositoryRoot, string absoluteDataPath)
    {
        var dataRoot = Resolve(repositoryRoot);
        var fullPath = Path.GetFullPath(absoluteDataPath);
        if (!IsWithin(dataRoot, fullPath))
            throw new ArgumentException("Path is outside the repository's Quality Studio data root.", nameof(absoluteDataPath));
        return ".quality/" + Path.GetRelativePath(dataRoot, fullPath).Replace('\\', '/');
    }

    public static string ResolveBasePath(string? configuredBasePath)
    {
        var value = string.IsNullOrWhiteSpace(configuredBasePath)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : configuredBasePath;
        return Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? DefaultBasePath : value);
    }

    private static string RepositoryIdentity(string root)
    {
        var name = SafeProjectId(new DirectoryInfo(root).Name);
        var identity = ReadOrigin(root) ?? root;
        identity = identity.Trim().TrimEnd('/');
        if (OperatingSystem.IsWindows()) identity = identity.ToLowerInvariant();
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        return $"{name}-{digest}";
    }

    private static string? ReadOrigin(string root)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("config");
            process.StartInfo.ArgumentList.Add("--get");
            process.StartInfo.ArgumentList.Add("remote.origin.url");
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string SafeProjectId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var builder = new StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
                separator = false;
            }
            else if (!separator && builder.Length > 0)
            {
                builder.Append('-');
                separator = true;
            }
        }
        var result = builder.ToString().Trim('-');
        return result.Length == 0 ? "repository" : result;
    }

    private static string RequireRepositoryRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        return Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void EnsureSeparate(string repositoryRoot, string dataRoot)
    {
        if (IsWithin(repositoryRoot, dataRoot) || IsWithin(dataRoot, repositoryRoot))
            throw new InvalidOperationException("Quality Studio data root must be separate from the repository checkout.");
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." || (!Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed record QualityDataMigrationResult(int FilesMoved, int ConflictsPreserved, int DirectoriesRemoved);

/// <summary>Moves the legacy distributed .quality tree into the external project data root once.</summary>
public sealed class QualityDataMigrator
{
    private const string MarkerFile = ".migration-v1.json";
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public async Task<QualityDataMigrationResult> MigrateAsync(
        string repositoryRoot,
        string? projectDataRoot = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var dataRoot = QualityDataRoot.Resolve(root, projectDataRoot);
        Directory.CreateDirectory(dataRoot);
        var marker = Path.Combine(dataRoot, MarkerFile);
        if (File.Exists(marker)) return new QualityDataMigrationResult(0, 0, 0);

        var qualityDirectories = EnumerateQualityDirectories(root)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path.Length)
            .ToArray();

        var moved = 0;
        var conflicts = 0;
        foreach (var qualityDirectory in qualityDirectories)
        {
            foreach (var source in Directory.EnumerateFiles(qualityDirectory, "*", Enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Destination(root, dataRoot, qualityDirectory, source);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination) && !FilesEqual(source, destination))
                {
                    destination = ConflictDestination(root, dataRoot, qualityDirectory, source);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    conflicts++;
                }
                if (!File.Exists(destination))
                {
                    // Copy then delete so migration also works when the checkout and data root
                    // are on different volumes. A missing marker makes an interrupted copy retryable.
                    File.Copy(source, destination, overwrite: false);
                    File.Delete(source);
                }
                else File.Delete(source);
                moved++;
            }
        }

        var removed = 0;
        foreach (var directory in qualityDirectories
                     .SelectMany(path => Directory.EnumerateDirectories(path, "*", Enumeration).Append(path))
                     .Distinct()
                     .OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                removed++;
            }
        }

        var document = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            repositoryRoot = root,
            migratedAt = DateTimeOffset.UtcNow,
            filesMoved = moved,
            conflictsPreserved = conflicts,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await AtomicFile.WriteAllTextAsync(marker, document + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return new QualityDataMigrationResult(moved, conflicts, removed);
    }

    private static string Destination(string root, string dataRoot, string qualityDirectory, string source)
    {
        var owner = Path.GetDirectoryName(qualityDirectory)!;
        var relative = Path.GetRelativePath(qualityDirectory, source);
        if (Path.GetFullPath(owner) == Path.GetFullPath(root))
            return Path.Combine(dataRoot, relative);
        if (relative.StartsWith("reviews" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Path.Combine(dataRoot, relative);
        return Path.Combine(dataRoot, "legacy", Path.GetRelativePath(root, owner), relative);
    }

    private static string ConflictDestination(string root, string dataRoot, string qualityDirectory, string source) =>
        Path.Combine(dataRoot, "migration-conflicts", Path.GetRelativePath(root, Path.GetDirectoryName(qualityDirectory)!),
            Path.GetRelativePath(qualityDirectory, source));

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length) return false;
        return SHA256.HashData(File.ReadAllBytes(left)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(right)));
    }

    private static IEnumerable<string> EnumerateQualityDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(directory);
                if (name.Equals(".quality", StringComparison.OrdinalIgnoreCase))
                {
                    yield return directory;
                    continue;
                }
                if (name is ".git" or "node_modules" or "bin" or "obj" or "dist") continue;
                pending.Push(directory);
            }
        }
    }
}
