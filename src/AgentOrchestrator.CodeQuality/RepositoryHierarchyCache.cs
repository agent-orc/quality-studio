using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

public sealed record RepositoryHierarchySnapshot(
    IReadOnlyList<HierarchyNode> Roots,
    string GitState,
    string ETag,
    string GitStateStatus = RepositoryGitState.OkStatus,
    string? GitStateDetail = null);

/// <summary>
/// The Git fingerprint a hierarchy snapshot is keyed on, and whether Git produced it. When Git is
/// missing or fails, the fingerprint is an explicit error marker rather than a filesystem walk of the
/// whole repository, so a broken Git never turns into a silent full scan through node_modules.
/// </summary>
public sealed record RepositoryGitState(string State, string Status, string? Detail)
{
    public const string OkStatus = "ok";
    public const string UnavailableStatus = "unavailable";
}

public sealed record RepositoryHierarchyMeasurement(
    RepositoryHierarchySnapshot Snapshot,
    bool CacheHit,
    double GitStatusMilliseconds,
    double CacheWaitMilliseconds,
    double ScanMilliseconds,
    double ReviewMetaDiscoveryMilliseconds,
    double TotalMilliseconds);

/// <summary>Caches one immutable hierarchy snapshot per repository and Git state.</summary>
public sealed class RepositoryHierarchyCache
{
    /// <summary>
    /// How long one Git fingerprint is reused before Git is asked again. Hashing the index and every
    /// dirty file is the dominant cost of a request, and a burst of requests describes the same commit;
    /// the price is that a change made inside this window is seen one beat late.
    /// </summary>
    public static readonly TimeSpan DefaultGitStateTtl = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GitStateEntry> gitStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan gitStateTtl;

    public RepositoryHierarchyCache(TimeSpan? gitStateTtl = null) =>
        this.gitStateTtl = gitStateTtl ?? DefaultGitStateTtl;

    public RepositoryHierarchySnapshot Get(
        string repositoryPath,
        InputResolver? inputResolver = null,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters) =>
        GetMeasured(repositoryPath, inputResolver, globalInputsDirectory, inputBudgetCharacters).Snapshot;

    public RepositoryHierarchyMeasurement GetMeasured(
        string repositoryPath,
        InputResolver? inputResolver = null,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var totalStarted = Stopwatch.GetTimestamp();
        var root = Path.GetFullPath(repositoryPath);
        var gitStatusStarted = Stopwatch.GetTimestamp();
        var git = GitState(root);
        var state = git.State + "\0" +
                    ComputeGlobalInputsState(globalInputsDirectory, inputBudgetCharacters);
        var gitStatusMilliseconds = Stopwatch.GetElapsedTime(gitStatusStarted).TotalMilliseconds;
        var slot = slots.GetOrAdd(root, _ => new CacheSlot());
        var cacheWaitStarted = Stopwatch.GetTimestamp();
        lock (slot.Gate)
        {
            var cacheWaitMilliseconds = Stopwatch.GetElapsedTime(cacheWaitStarted).TotalMilliseconds;
            if (slot.Snapshot is not null && StringComparer.Ordinal.Equals(slot.Snapshot.GitState, state))
            {
                return new RepositoryHierarchyMeasurement(
                    slot.Snapshot,
                    true,
                    gitStatusMilliseconds,
                    cacheWaitMilliseconds,
                    0,
                    0,
                    Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds);
            }

            var scanStarted = Stopwatch.GetTimestamp();
            var hierarchy = RepositoryHierarchyBuilder.Build(root);
            var scanMilliseconds = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
            var discoveryStarted = Stopwatch.GetTimestamp();
            ReviewMetaDiscovery.AttachDiscovered(
                root, hierarchy, inputResolver, globalInputsDirectory, inputBudgetCharacters);
            var reviewMetaDiscoveryMilliseconds = Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds;
            var etagHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
            slot.Snapshot = new RepositoryHierarchySnapshot(
                hierarchy, state, $"\"{etagHash}\"", git.Status, git.Detail);
            return new RepositoryHierarchyMeasurement(
                slot.Snapshot,
                false,
                gitStatusMilliseconds,
                cacheWaitMilliseconds,
                scanMilliseconds,
                reviewMetaDiscoveryMilliseconds,
                Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds);
        }
    }

    /// <summary>The current Git fingerprint, reused for <see cref="gitStateTtl"/> before Git runs again.</summary>
    private RepositoryGitState GitState(string root)
    {
        if (gitStateTtl > TimeSpan.Zero &&
            gitStates.TryGetValue(root, out var cached) &&
            Stopwatch.GetElapsedTime(cached.Timestamp) < gitStateTtl)
        {
            return cached.State;
        }

        var computed = ComputeGitState(root);
        gitStates[root] = new GitStateEntry(computed, Stopwatch.GetTimestamp());
        return computed;
    }

    private static RepositoryGitState ComputeGitState(string root)
    {
        var head = RunGit(root, "rev-parse", "--verify", "HEAD") ?? "unborn";
        var index = RunGit(root, "ls-files", "--stage", "-z") ?? "no-index";
        var status = RunGit(root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status is null)
        {
            // Never fall back to walking the tree: that scan reads node_modules and every build output,
            // takes minutes on a real repository, and hides the actual failure.
            return new RepositoryGitState(
                "git-unavailable",
                RepositoryGitState.UnavailableStatus,
                "git status failed in this repository, so the hierarchy cannot follow the working tree. " +
                "Check that git is on PATH and that the directory is a readable Git repository.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, head);
        Append(hash, index);
        var entries = ParseStatusPaths(status).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var relativePath in entries)
        {
            Append(hash, relativePath);
            var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                Append(hash, "deleted");
                continue;
            }

            using var stream = File.OpenRead(absolutePath);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
        }
        return new RepositoryGitState(
            Convert.ToHexStringLower(hash.GetHashAndReset()), RepositoryGitState.OkStatus, null);
    }

    private static string ComputeGlobalInputsState(string? directory, int budgetCharacters)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, budgetCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (string.IsNullOrWhiteSpace(directory))
        {
            Append(hash, "none");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        var root = Path.GetFullPath(directory);
        Append(hash, root);
        if (!Directory.Exists(root))
        {
            Append(hash, "missing");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                     .Order(StringComparer.Ordinal))
        {
            Append(hash, Path.GetFileName(path));
            using var stream = File.OpenRead(path);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static IEnumerable<string> ParseStatusPaths(string status)
    {
        var records = status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            yield return record[3..].Replace('\\', '/');
            if (record[0] is 'R' or 'C' || record[1] is 'R' or 'C')
            {
                if (++index < records.Length) yield return records[index].Replace('\\', '/');
            }
        }
    }

    private static string? RunGit(string root, params string[] arguments)
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
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private sealed class CacheSlot
    {
        public object Gate { get; } = new();
        public RepositoryHierarchySnapshot? Snapshot { get; set; }
    }

    private sealed record GitStateEntry(RepositoryGitState State, long Timestamp);
}
