using System.Collections.Concurrent;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

/// <summary>Indexes review sidecars once per repository and keeps the index current from filesystem events.</summary>
public sealed class ReviewMetaIndex : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<RepositoryIndex>> repositories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyList<JsonElement> Read(string root, string relativePath) =>
        Get(root).Read(relativePath);

    public string Find(string root, string relativePath, string kind) =>
        Get(root).Find(relativePath, kind);

    /// <summary>
    /// Drops the index and its filesystem watcher for one repository. Archiving a registration means
    /// nothing will read its sidecars again, and an OS watch handle per archived repository is a leak.
    /// </summary>
    public void Release(string root)
    {
        if (repositories.TryRemove(Path.GetFullPath(root), out var index)) index.Value.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in repositories.Keys) Release(key);
    }

    // ConcurrentDictionary.GetOrAdd may run its value factory more than once for the same
    // key and keeps only one result. Each run constructed and enabled a FileSystemWatcher,
    // so every discarded instance leaked a live directory handle that went on receiving
    // events for the rest of the process. Lazy with ExecutionAndPublication builds exactly
    // one watcher per root, and makes Release deterministic against a concurrent first read.
    private RepositoryIndex Get(string root) => repositories.GetOrAdd(
        Path.GetFullPath(root),
        static path => new Lazy<RepositoryIndex>(
            () => new RepositoryIndex(path), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private sealed class RepositoryIndex : IDisposable
    {
        private static readonly EnumerationOptions ConfinedEnumeration = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        private readonly object gate = new();
        private readonly string root;
        private readonly Dictionary<string, IndexedDocument> documents =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly FileSystemWatcher watcher;

        public RepositoryIndex(string root)
        {
            this.root = root;
            foreach (var path in Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                         .Where(IsReviewMetaPath))
                Update(path);

            watcher = new FileSystemWatcher(root, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                // A review sweep rewrites many sidecars at once. The default 8 KiB kernel buffer
                // overflows under that burst and the notifications in it are lost silently.
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, args) => Update(args.FullPath);
            watcher.Changed += (_, args) => Update(args.FullPath);
            watcher.Deleted += (_, args) => Remove(args.FullPath);
            watcher.Renamed += (_, args) =>
            {
                Remove(args.OldFullPath);
                Update(args.FullPath);
            };
            // Buffer overflow or a lost watch handle: rebuild rather than serve an index that silently
            // stopped following the repository.
            watcher.Error += (_, _) => Reindex();
        }

        /// <summary>Rebuilds the whole index from disk after the watcher lost events.</summary>
        private void Reindex()
        {
            try
            {
                var rebuilt = Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                    .Where(IsReviewMetaPath).ToArray();
                lock (gate) documents.Clear();
                foreach (var path in rebuilt) Update(path);
                try
                {
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException or InvalidOperationException or IOException)
                {
                    // The index stays correct as of this rebuild; a disposed watcher has nothing to re-arm.
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // The repository directory went away underneath the watcher; the registry reports that.
            }
        }

        public IReadOnlyList<JsonElement> Read(string relativePath)
        {
            lock (gate)
            {
                return documents.Values
                    .Where(document => string.Equals(document.UnitPath, relativePath, StringComparison.Ordinal))
                    .Select(document => document.Payload.Clone()).ToArray();
            }
        }

        public string Find(string relativePath, string kind)
        {
            lock (gate)
            {
                return documents.Values.FirstOrDefault(document =>
                           string.Equals(document.UnitPath, relativePath, StringComparison.Ordinal) &&
                           string.Equals(document.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                           File.Exists(document.Path))?.Path
                       ?? throw new FileNotFoundException(
                           $"No {kind} review metadata exists for '{relativePath}'.", relativePath);
            }
        }

        public void Dispose()
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        private void Update(string path)
        {
            if (!IsReviewMetaPath(path) || !File.Exists(path)) return;
            try
            {
                if (!PathConfinement.IsWithin(root, path)) return;
                PathConfinement.RejectReparseTraversal(root, path);
                // The one reader decides what a sidecar says and reports what it cannot read; the
                // raw payload is kept because callers still project over the whole document.
                if (!ReviewMetaReader.TryLoad(path, out var sidecar, out _)) return;
                var storedPath = sidecar.Document.Unit.Path.Replace('\\', '/').TrimStart('/');
                var absoluteSubject = Path.GetFullPath(Path.Combine(root,
                    storedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathConfinement.IsWithin(root, absoluteSubject)) return;
                PathConfinement.RejectReparseTraversal(root, absoluteSubject);
                using var parsed = JsonDocument.Parse(sidecar.Json);
                var normalized = Path.GetRelativePath(root, absoluteSubject).Replace('\\', '/');
                lock (gate)
                    documents[path] = new IndexedDocument(path, normalized,
                        sidecar.Document.Kind.ToString().ToLowerInvariant(), parsed.RootElement.Clone());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // A writer can briefly expose a changed notification before its atomic replace settles.
                // The subsequent rename/change notification will retry; malformed sidecars stay unindexed.
            }
        }

        private void Remove(string path)
        {
            // Sidecars are replaced with File.Move(overwrite: true). Windows reports that as a
            // delete of the destination followed by a rename onto it, while the file itself never
            // leaves the disk. Dropping the document on that delete opened a window in which a
            // read found no review metadata for a file that was there the whole time - a request
            // arriving in it failed with "no metadata exists". Only forget a path that is gone.
            if (File.Exists(path)) return;
            lock (gate) documents.Remove(path);
        }

        private static bool IsReviewMetaPath(string path) =>
            path.Contains(".review-meta.", StringComparison.Ordinal) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private sealed record IndexedDocument(string Path, string UnitPath, string Kind, JsonElement Payload);
    }
}
