using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

public sealed record RepositoryHierarchySnapshot(
    IReadOnlyList<HierarchyNode> Roots,
    string GitState,
    string ETag);

public sealed record RepositoryHierarchyMeasurement(
    RepositoryHierarchySnapshot Snapshot,
    bool CacheHit,
    double GitStatusMilliseconds,
    double CacheWaitMilliseconds,
    double ScanMilliseconds,
    double ReviewMetaDiscoveryMilliseconds,
    double TotalMilliseconds);

public sealed record RepositoryStateMeasurement(
    string State,
    string HeadSha,
    double DurationMilliseconds);

/// <summary>Caches one immutable hierarchy snapshot per repository and Git state.</summary>
public sealed class RepositoryHierarchyCache
{
    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);

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
        var stateMeasurement = MeasureState(root, globalInputsDirectory, inputBudgetCharacters);
        var state = stateMeasurement.State;
        var gitStatusMilliseconds = stateMeasurement.DurationMilliseconds;
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
            slot.Snapshot = new RepositoryHierarchySnapshot(hierarchy, state, $"\"{etagHash}\"");
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

    /// <summary>
    /// Computes the correctness key used by both the memory cache and API-owned
    /// persistent snapshots. HEAD is retained separately for diagnostics while the
    /// state also covers index, dirty/untracked content, global inputs, and budget.
    /// </summary>
    public RepositoryStateMeasurement MeasureState(
        string repositoryPath,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var started = Stopwatch.GetTimestamp();
        var root = Path.GetFullPath(repositoryPath);
        var head = RunGit(root, "rev-parse", "--verify", "HEAD") ?? "unborn";
        var state = ComputeGitState(root, head) + "\0" +
                    ComputeGlobalInputsState(globalInputsDirectory, inputBudgetCharacters);
        return new RepositoryStateMeasurement(
            state,
            head,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Seeds a previously verified immutable snapshot for this repository.</summary>
    public void Seed(string repositoryPath, RepositoryHierarchySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = Path.GetFullPath(repositoryPath);
        var slot = slots.GetOrAdd(root, _ => new CacheSlot());
        lock (slot.Gate)
        {
            slot.Snapshot = snapshot;
        }
    }

    /// <summary>
    /// Returns the immutable snapshot already selected by a client without repeating Git-state
    /// measurement. This is only valid when the caller supplies the exact snapshot ETag returned
    /// by the preceding root response.
    /// </summary>
    public bool TryGetSeeded(string repositoryPath, string etag, out RepositoryHierarchySnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        var root = Path.GetFullPath(repositoryPath);
        if (slots.TryGetValue(root, out var slot))
        {
            lock (slot.Gate)
            {
                if (slot.Snapshot is not null && StringComparer.Ordinal.Equals(slot.Snapshot.ETag, etag))
                {
                    snapshot = slot.Snapshot;
                    return true;
                }
            }
        }

        snapshot = null!;
        return false;
    }

    private static string ComputeGitState(string root, string head)
    {
        var index = RunGit(root, "ls-files", "--stage", "-z") ?? "no-index";
        var status = RunGit(root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status is null)
        {
            return ComputeFilesystemState(root);
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
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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

    private static string ComputeFilesystemState(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part == ".git"))
                     .Order(StringComparer.Ordinal))
        {
            Append(hash, Path.GetRelativePath(root, path).Replace('\\', '/'));
            var info = new FileInfo(path);
            Append(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
}
