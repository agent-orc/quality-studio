using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentOrchestrator.CodeQuality;

public sealed record RepositoryHierarchySnapshot(
    IReadOnlyList<HierarchyNode> Roots,
    string GitState,
    string ETag);

/// <summary>Caches one immutable hierarchy snapshot per repository and Git state.</summary>
public sealed class RepositoryHierarchyCache
{
    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);

    public RepositoryHierarchySnapshot Get(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = Path.GetFullPath(repositoryPath);
        var state = ComputeGitState(root);
        var slot = slots.GetOrAdd(root, _ => new CacheSlot());
        lock (slot.Gate)
        {
            if (slot.Snapshot is not null && StringComparer.Ordinal.Equals(slot.Snapshot.GitState, state))
            {
                return slot.Snapshot;
            }

            var hierarchy = RepositoryHierarchyBuilder.Build(root);
            ReviewMetaDiscovery.AttachDiscovered(root, hierarchy);
            var etagHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
            slot.Snapshot = new RepositoryHierarchySnapshot(hierarchy, state, $"\"{etagHash}\"");
            return slot.Snapshot;
        }
    }

    private static string ComputeGitState(string root)
    {
        var head = RunGit(root, "rev-parse", "--verify", "HEAD") ?? "unborn";
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
