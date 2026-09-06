using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Migrates legacy in-tree data and reports checkout contamination during API startup.</summary>
public sealed class QualityDataStartupService(
    RepositoryRegistry repositories,
    ILogger<QualityDataStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var repository in repositories.List(includeArchived: true))
        {
            var dirtyPaths = await GitQualityStatusAsync(repository.RootPath, cancellationToken).ConfigureAwait(false);
            if (dirtyPaths.Count > 0)
            {
                logger.LogWarning(new EventId(1410, "DirtyInTreeQualityData"),
                    "Repository {RepositoryId} has dirty in-tree .quality data: {DirtyPaths}. " +
                    "Quality Studio runtime data belongs under {DataRootPath}.",
                    repository.Id, string.Join(", ", dirtyPaths), repository.DataRootPath);
            }

            var result = await new QualityDataMigrator().MigrateAsync(
                repository.RootPath, repository.DataRootPath, cancellationToken).ConfigureAwait(false);
            if (result.FilesMoved > 0)
            {
                logger.LogInformation(new EventId(1411, "QualityDataMigrated"),
                    "Migrated {FileCount} Quality Studio files for {RepositoryId} to {DataRootPath}; " +
                    "{ConflictCount} conflicts were preserved.",
                    result.FilesMoved, repository.Id, repository.DataRootPath, result.ConflictsPreserved);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static async Task<IReadOnlyList<string>> GitQualityStatusAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")) &&
            !File.Exists(Path.Combine(repositoryRoot, ".git"))) return [];
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
                 {
                     "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching", "--",
                     ".quality", ":(glob)**/.quality/**",
                 }) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return [];
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Length > 3 ? line[3..] : line)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }
}
