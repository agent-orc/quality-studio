using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

/// <summary>Resolves and initializes checkout-external storage for registered repositories.</summary>
public sealed class QualityDataRoot
{
    private readonly string projectsRoot;

    public QualityDataRoot(IHostEnvironment environment, IOptions<RepositoryOptions> options)
    {
        var configured = options.Value.DataRoot;
        if (string.IsNullOrWhiteSpace(configured))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            projectsRoot = Path.Combine(local, "QualityStudio", "projects");
        }
        else
        {
            projectsRoot = Path.GetFullPath(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(environment.ContentRootPath, configured));
        }
    }

    public string ProjectsRoot => projectsRoot;

    public string ForProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId) ||
            projectId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Project id is not safe for use as a data-root key.", nameof(projectId));
        return Path.Combine(projectsRoot, projectId.ToLowerInvariant());
    }
}

public sealed record QualityDataMigrationResult(int FilesMoved, int DirectoriesRemoved, bool AlreadyCompleted);

/// <summary>
/// One-time migration of every in-checkout .quality tree to the project's external data mirror.
/// Relative paths are retained, so sidecars beside source folders remain addressable without
/// placing them in the checkout.
/// </summary>
public sealed class QualityDataMigrator(ILogger<QualityDataMigrator> logger)
{
    private const string MarkerName = ".migration-v1.json";

    public QualityDataMigrationResult Migrate(string repositoryRoot, string dataRoot)
    {
        var sourceRoot = Path.GetFullPath(repositoryRoot);
        var targetRoot = Path.GetFullPath(dataRoot);
        if (PathConfinement.IsWithin(sourceRoot, targetRoot))
            throw new InvalidOperationException("Quality Studio data root must be outside the analysed checkout.");

        var marker = Path.Combine(targetRoot, MarkerName);
        if (File.Exists(marker))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var recordedRoot = document.RootElement.TryGetProperty("repositoryRoot", out var value)
                ? value.GetString()
                : null;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(recordedRoot, sourceRoot, comparison))
                throw new InvalidOperationException(
                    $"Project data root is already assigned to a different repository: {targetRoot}");
            return new QualityDataMigrationResult(0, 0, true);
        }

        Directory.CreateDirectory(targetRoot);
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
            var relativeDirectory = Path.GetRelativePath(sourceRoot, qualityDirectory);
            foreach (var source in Directory.EnumerateFiles(qualityDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            }))
            {
                var relativeFile = Path.GetRelativePath(qualityDirectory, source);
                var destination = Path.Combine(targetRoot, relativeDirectory, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(destination)))
                        throw new IOException($"Migration destination already contains different data: {destination}");
                    File.Delete(source);
                }
                else
                {
                    try
                    {
                        File.Move(source, destination);
                    }
                    catch (IOException)
                    {
                        // File.Move cannot cross volumes on every supported platform. CreateNew
                        // preserves conflict safety; deletion happens only after a complete copy.
                        var copied = false;
                        var created = false;
                        try
                        {
                            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                                       FileShare.None, 81920, FileOptions.WriteThrough))
                            {
                                created = true;
                                input.CopyTo(output);
                                output.Flush(flushToDisk: true);
                            }
                            copied = true;
                            File.Delete(source);
                        }
                        finally
                        {
                            if (created && !copied && File.Exists(destination)) File.Delete(destination);
                        }
                    }
                }
                moved++;
            }
        }

        var removed = 0;
        foreach (var directory in qualityDirectories)
        {
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length))
                if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                removed++;
            }
        }

        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            repositoryRoot = sourceRoot,
            migratedAt = DateTimeOffset.UtcNow,
            filesMoved = moved,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + Environment.NewLine);
        logger.LogInformation(new EventId(1410, "QualityDataMigrated"),
            "Migrated {FileCount} Quality Studio data files for {RepositoryRoot} to {DataRoot}",
            moved, sourceRoot, targetRoot);
        return new QualityDataMigrationResult(moved, removed, false);
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
        process.StartInfo.ArgumentList.Add("**/.quality/**");
        process.StartInfo.ArgumentList.Add(".quality/**");
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }
}

/// <summary>Runs the warning and migration before other hosted services start scanning repositories.</summary>
public sealed class QualityDataInitializer(
    RepositoryRegistry repositories,
    QualityDataMigrator migrator,
    ILogger<QualityDataInitializer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var repository in repositories.List(includeArchived: true))
        {
            var dirty = QualityDataMigrator.DirtyQualityPaths(repository.RootPath);
            if (dirty.Count > 0)
                logger.LogWarning(new EventId(1411, "DirtyQualityTree"),
                    "Repository {RepositoryId} has dirty in-checkout .quality data: {DirtyPaths}",
                    repository.Id, string.Join(", ", dirty));
            migrator.Migrate(repository.RootPath, repository.DataRootPath);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
