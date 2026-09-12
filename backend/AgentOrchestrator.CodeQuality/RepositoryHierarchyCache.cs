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

    /// <summary>The resolved HEAD commit behind this fingerprint, carried for diagnostics only.</summary>
    public string Head { get; init; } = "unborn";

    /// <summary>Source and scope state, excluding review documents and review inputs.</summary>
    public string StructureState { get; init; } = State;
}

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
    /// <summary>
    /// How long one Git fingerprint is reused before Git is asked again. Hashing the index and every
    /// dirty file is the dominant cost of a request, and a burst of requests describes the same commit;
    /// the price is that a change made inside this window is seen one beat late.
    /// </summary>
    public static readonly TimeSpan DefaultGitStateTtl = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, CacheSlot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GitStateEntry> gitStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> gitStateGates = new(StringComparer.OrdinalIgnoreCase);
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
        var slot = slots.GetOrAdd(root, _ => new CacheSlot());
        var cacheWaitStarted = Stopwatch.GetTimestamp();
        lock (slot.Gate)
        {
            var cacheWaitMilliseconds = Stopwatch.GetElapsedTime(cacheWaitStarted).TotalMilliseconds;
            // Measure after waiting: a queued reader must not rebuild a state superseded while
            // another reader was deriving the hierarchy.
            var gitStatusStarted = Stopwatch.GetTimestamp();
            var git = GitState(root);
            var state = git.State + "\0" +
                        ComputeGlobalInputsState(globalInputsDirectory, inputBudgetCharacters);
            var gitStatusMilliseconds = Stopwatch.GetElapsedTime(gitStatusStarted).TotalMilliseconds;
            if (slot.Snapshot is not null && StringComparer.Ordinal.Equals(slot.Snapshot.GitState, state))
            {
                slot.StructureState ??= git.StructureState;
                return new RepositoryHierarchyMeasurement(
                    slot.Snapshot,
                    true,
                    gitStatusMilliseconds,
                    cacheWaitMilliseconds,
                    0,
                    0,
                    Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds);
            }

            double scanMilliseconds = 0;
            if (slot.Structure is null || !StringComparer.Ordinal.Equals(slot.StructureState, git.StructureState))
            {
                var scanStarted = Stopwatch.GetTimestamp();
                slot.Structure = RepositoryHierarchyBuilder.Build(root);
                slot.StructureState = git.StructureState;
                scanMilliseconds = Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds;
            }
            // Metadata changes need fresh attachments, not another MSBuild/Roslyn derivation.
            // Clone canonical aliases together so published snapshots remain immutable.
            var hierarchy = CloneStructure(slot.Structure);
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

    /// <summary>
    /// The correctness key shared by the memory cache and the API-owned persistent snapshots. HEAD
    /// is carried separately for diagnostics while the state also covers the index, dirty and
    /// untracked content, the global inputs, and the budget.
    /// </summary>
    public RepositoryStateMeasurement MeasureState(
        string repositoryPath,
        string? globalInputsDirectory = null,
        int inputBudgetCharacters = InputResolver.DefaultBudgetCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var started = Stopwatch.GetTimestamp();
        var root = Path.GetFullPath(repositoryPath);
        var git = GitState(root);
        var state = git.State + "\0" +
                    ComputeGlobalInputsState(globalInputsDirectory, inputBudgetCharacters);
        return new RepositoryStateMeasurement(
            state,
            git.Head,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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

        lock (gitStateGates.GetOrAdd(root, _ => new object()))
        {
            if (gitStateTtl > TimeSpan.Zero &&
                gitStates.TryGetValue(root, out cached) &&
                Stopwatch.GetElapsedTime(cached.Timestamp) < gitStateTtl)
            {
                return cached.State;
            }
            var computed = ComputeGitState(root);
            gitStates[root] = new GitStateEntry(computed, Stopwatch.GetTimestamp());
            return computed;
        }
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
            slot.Structure = CloneStructure(snapshot.Roots);
            slot.StructureState = null;
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
                "Check that git is on PATH and that the directory is a readable Git repository.")
            { Head = head };
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var structureHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, head);
        Append(hash, index);
        foreach (var entry in index.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            var path = separator < 0 ? entry : entry[(separator + 1)..];
            if (!IsReviewMetadataPath(path)) Append(structureHash, entry);
        }
        var entries = GitStatusPaths.Parse(status).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var relativePath in entries)
        {
            Append(hash, relativePath);
            var affectsStructure = !IsReviewMetadataPath(relativePath);
            if (affectsStructure) Append(structureHash, relativePath);
            var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                Append(hash, "deleted");
                if (affectsStructure) Append(structureHash, "deleted");
                continue;
            }

            using var stream = File.OpenRead(absolutePath);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                if (affectsStructure) structureHash.AppendData(buffer, 0, read);
            }
        }
        // Generated reviews live outside Git; an unchanged checkout can still gain, replace,
        // or lose a review. Include their content in the snapshot key but not the source key.
        foreach (var path in ReviewMetaPath.Enumerate(root).Order(StringComparer.Ordinal))
        {
            Append(hash, ReviewMetaPath.Describe(root, path));
            try
            {
                using var stream = File.OpenRead(path);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
            }
            catch (IOException) when (!File.Exists(path))
            {
                Append(hash, "deleted");
            }
        }
        return new RepositoryGitState(
            Convert.ToHexStringLower(hash.GetHashAndReset()), RepositoryGitState.OkStatus, null)
        {
            Head = head,
            StructureState = Convert.ToHexStringLower(structureHash.GetHashAndReset()),
        };
    }

    private static bool IsReviewMetadataPath(string path)
    {
        var normalized = "/" + path.Replace('\\', '/');
        return normalized.Contains("/.quality/inputs/", StringComparison.Ordinal) ||
               (normalized.Contains(".review-meta.", StringComparison.Ordinal) &&
                normalized.EndsWith(".json", StringComparison.Ordinal));
    }

    private static IReadOnlyList<HierarchyNode> CloneStructure(IReadOnlyList<HierarchyNode> roots)
    {
        var copies = new Dictionary<HierarchyNode, HierarchyNode>(ReferenceEqualityComparer.Instance);
        HierarchyNode Clone(HierarchyNode node)
        {
            if (copies.TryGetValue(node, out var existing)) return existing;
            var copy = new HierarchyNode(node.Id, node.Name, node.Level, node.Path, node.SizeBytes, node.LineCount);
            copies.Add(node, copy);
            copy.AddExclusions(node.Exclusions);
            foreach (var child in node.Children) copy.AddChild(Clone(child));
            return copy;
        }
        return roots.Select(Clone).ToArray();
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
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return null;
            // Drain stderr concurrently; an unread redirected stream blocks git once its pipe is full.
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
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
        public IReadOnlyList<HierarchyNode>? Structure { get; set; }
        public string? StructureState { get; set; }
    }

    private sealed record GitStateEntry(RepositoryGitState State, long Timestamp);
}
