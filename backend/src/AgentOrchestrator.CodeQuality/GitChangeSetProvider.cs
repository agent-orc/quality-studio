using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

public sealed class GitMergeRangeChangeSetProvider : IChangeSetProvider
{
    public string Id => "git-merge-range";

    public async Task<IReadOnlyList<ChangeSet>> GetAsync(
        ChangeSetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var root = GitPlumbing.RequireRepository(query.RepositoryRoot);
        if (query.Count < 1) throw new ArgumentOutOfRangeException(nameof(query), "Count must be at least one.");

        if (!string.IsNullOrWhiteSpace(query.BaseRevision))
        {
            var @base = await GitPlumbing.ResolveCommitAsync(root, query.BaseRevision, cancellationToken).ConfigureAwait(false);
            var result = await GitPlumbing.ResolveCommitAsync(root, query.HeadRevision, cancellationToken).ConfigureAwait(false);
            return [await CreateAsync(root, @base, result, cancellationToken).ConfigureAwait(false)];
        }

        var tip = string.IsNullOrWhiteSpace(query.IntegrationBranch)
            ? query.HeadRevision
            : query.IntegrationBranch;
        var output = await GitPlumbing.RunAsync(
            root, ["rev-list", "--first-parent", $"--max-count={query.Count}", tip],
            cancellationToken).ConfigureAwait(false);
        var commits = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resultSets = new List<ChangeSet>(commits.Length);
        foreach (var commit in commits)
        {
            var parents = await ParentsAsync(root, commit, cancellationToken).ConfigureAwait(false);
            if (parents.Count == 0) continue;
            resultSets.Add(await CreateAsync(root, parents[0], commit, cancellationToken).ConfigureAwait(false));
        }

        return resultSets;
    }

    private async Task<ChangeSet> CreateAsync(
        string root,
        string baseCommit,
        string resultCommit,
        CancellationToken cancellationToken)
    {
        var parents = await ParentsAsync(root, resultCommit, cancellationToken).ConfigureAwait(false);
        var isMerge = parents.Count > 1 && StringComparer.Ordinal.Equals(parents[0], baseCommit);
        var headCommit = isMerge ? parents[1] : resultCommit;
        var mergeCommit = isMerge ? resultCommit : null;
        var metadata = await GitPlumbing.RunAsync(
            root, ["show", "-s", "--format=%s%x00%cI", resultCommit],
            cancellationToken).ConfigureAwait(false);
        var parts = metadata.TrimEnd('\n').Split('\0');
        if (parts.Length != 2 ||
            !DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var committedAt))
        {
            throw new ChangeReviewException($"Git returned invalid commit metadata for '{resultCommit}'.");
        }

        var touched = await ReadTouchedPathsAsync(root, baseCommit, resultCommit, cancellationToken)
            .ConfigureAwait(false);
        return new ChangeSet(Id, baseCommit, headCommit, mergeCommit, parts[0], committedAt, touched);
    }

    private static async Task<IReadOnlyList<string>> ParentsAsync(
        string root,
        string commit,
        CancellationToken cancellationToken)
    {
        var line = (await GitPlumbing.RunAsync(
            root, ["show", "-s", "--format=%P", commit], cancellationToken).ConfigureAwait(false)).Trim();
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task<IReadOnlyList<ChangedPath>> ReadTouchedPathsAsync(
        string root,
        string baseCommit,
        string resultCommit,
        CancellationToken cancellationToken)
    {
        var nameStatus = await GitPlumbing.RunAsync(
            root, ["diff", "--find-renames", "--find-copies", "--name-status", "-z", baseCommit, resultCommit],
            cancellationToken).ConfigureAwait(false);
        var stats = await ReadNumStatsAsync(root, baseCommit, resultCommit, cancellationToken).ConfigureAwait(false);
        var tokens = nameStatus.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<ChangedPath>();
        for (var index = 0; index < tokens.Length;)
        {
            var status = tokens[index++];
            var code = status[0];
            string? previous = null;
            string path;
            if (code is 'R' or 'C')
            {
                if (index + 1 >= tokens.Length) throw new ChangeReviewException("Git returned an invalid rename record.");
                previous = Normalize(tokens[index++]);
                path = Normalize(tokens[index++]);
            }
            else
            {
                if (index >= tokens.Length) throw new ChangeReviewException("Git returned an invalid path record.");
                path = Normalize(tokens[index++]);
            }

            var kind = code switch
            {
                'A' => ChangeKind.Added,
                'D' => ChangeKind.Deleted,
                'R' => ChangeKind.Renamed,
                'C' => ChangeKind.Copied,
                _ => ChangeKind.Modified,
            };
            var key = previous is null ? path : previous + "\0" + path;
            stats.TryGetValue(key, out var stat);
            var contentChanged = kind is not (ChangeKind.Renamed or ChangeKind.Copied) ||
                                 !status.EndsWith("100", StringComparison.Ordinal);
            result.Add(new ChangedPath(
                path, kind, previous, stat.Additions, stat.Deletions, contentChanged,
                stat.Additions is null && stat.Deletions is null));
        }

        return result.OrderBy(path => path.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task<Dictionary<string, (int? Additions, int? Deletions)>> ReadNumStatsAsync(
        string root,
        string baseCommit,
        string resultCommit,
        CancellationToken cancellationToken)
    {
        var output = await GitPlumbing.RunAsync(
            root, ["diff", "--find-renames", "--find-copies", "--numstat", "-z", baseCommit, resultCommit],
            cancellationToken).ConfigureAwait(false);
        var tokens = output.Split('\0');
        var result = new Dictionary<string, (int?, int?)>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Length && tokens[index].Length > 0; index++)
        {
            var columns = tokens[index].Split('\t');
            if (columns.Length != 3) continue;
            var additions = ParseCount(columns[0]);
            var deletions = ParseCount(columns[1]);
            if (columns[2].Length > 0)
            {
                result[Normalize(columns[2])] = (additions, deletions);
            }
            else if (index + 2 < tokens.Length)
            {
                var oldPath = Normalize(tokens[++index]);
                var newPath = Normalize(tokens[++index]);
                result[oldPath + "\0" + newPath] = (additions, deletions);
            }
        }

        return result;
    }

    private static int? ParseCount(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal static class GitPlumbing
{
    public static string RequireRepository(string path)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository path does not exist: {root}");
        try
        {
            var top = RunAsync(root, ["rev-parse", "--show-toplevel"], CancellationToken.None)
                .GetAwaiter().GetResult().Trim();
            return Path.GetFullPath(top);
        }
        catch (ChangeReviewException exception)
        {
            throw new ChangeReviewException($"'{root}' is not a readable Git repository.", exception);
        }
    }

    public static async Task<string> ResolveCommitAsync(
        string root,
        string revision,
        CancellationToken cancellationToken) =>
        (await RunAsync(root, ["rev-parse", "--verify", revision + "^{commit}"], cancellationToken)
            .ConfigureAwait(false)).Trim();

    public static async Task<string> RunAsync(
        string root,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowMissingPath = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new ChangeReviewException("Git did not start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ChangeReviewException("Git is required for change-set review.", exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            if (allowMissingPath && process.ExitCode == 128) return string.Empty;
            throw new ChangeReviewException(
                $"Git {arguments[0]} failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output;
    }
}
