using System.Diagnostics;
using System.Globalization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The commit a review run observed. Captured once when the run is planned so that an exported
/// report stays pinned to the code it reviewed, even after the working tree has moved on.
/// <c>Dirty</c> is null when the working-tree probe itself failed, so a failed probe is never
/// reported as a clean tree.
/// </summary>
public sealed record QualityRunSourceRevision(
    string CommitSha,
    string ShortCommitSha,
    string? Branch,
    bool? Dirty,
    DateTimeOffset? CommittedAt);

/// <summary>
/// Reads the repository head revision through the Git CLI. Every failure mode - no Git binary, no
/// repository, an unborn head, a slow probe - is reported as an absent value rather than an
/// exception, because a review run must never fail over missing provenance. Git resolves the
/// repository by walking up from the given path, so a registered subdirectory is pinned to the
/// commit of the working tree that contains it.
/// </summary>
public static class GitSourceRevisionReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<QualityRunSourceRevision?> ReadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return null;
        // One process answers both the head identity and its commit time; it also fails cleanly when
        // the path is not a working tree or the head is unborn.
        var head = await RunGitAsync(repositoryRoot, cancellationToken, "show", "-s", "--format=%H%n%cI", "HEAD")
            .ConfigureAwait(false);
        var lines = head?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (lines.Length == 0 || lines[0].Length < 7) return null;
        var commit = lines[0].Trim();

        var branch = await RunGitAsync(repositoryRoot, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD")
            .ConfigureAwait(false);
        // Untracked files count as a modified tree: review targets come from the filesystem, so an
        // uncommitted new file is reviewed even though it does not exist at the pinned commit.
        var status = await RunGitAsync(repositoryRoot, cancellationToken, "status", "--porcelain=v1")
            .ConfigureAwait(false);
        return new QualityRunSourceRevision(
            commit,
            commit[..Math.Min(12, commit.Length)],
            string.IsNullOrEmpty(branch) || branch == "HEAD" ? null : branch,
            status is null ? null : status.Length > 0,
            lines.Length > 1 && DateTimeOffset.TryParse(
                lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at.ToUniversalTime()
                : null);
    }

    private static async Task<string?> RunGitAsync(
        string root,
        CancellationToken cancellationToken,
        params string[] arguments)
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            if (!process.Start()) return null;
            // Both pipes are drained concurrently: an unread stderr would fill its buffer and block
            // git forever while this side waited on stdout.
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var diagnostics = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, diagnostics).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Result.TrimEnd('\r', '\n') : null;
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or IOException)
        {
            Kill(process);
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
        {
        }
    }
}
