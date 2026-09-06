using System.Collections.Concurrent;
using System.Text.Json;

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
    /// Closes the watcher of a repository that is no longer served and drops its index.
    /// A later read rebuilds both from disk. Without this a root stayed watched forever,
    /// which on Windows keeps an open directory handle on a repository nobody serves.
    /// </summary>
    public void Forget(string root)
    {
        if (repositories.TryRemove(Path.GetFullPath(root), out var index)) index.Value.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in repositories.Keys) Forget(key);
    }

    // ConcurrentDictionary.GetOrAdd may run its value factory more than once for the same
    // key and keeps only one result. Each run constructed and enabled a FileSystemWatcher,
    // so every discarded instance leaked a live directory handle that went on receiving
    // events for the rest of the process. Lazy with ExecutionAndPublication builds exactly
    // one watcher per root, and makes Forget deterministic against a concurrent first read.
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
                // Share the file with writers and deleters: sidecars are replaced with
                // File.Move(overwrite: true), and a plain read holds a handle that denies the
                // replace, so indexing a sidecar could make the write of the next one fail.
                using var stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    Options = FileOptions.SequentialScan,
                });
                using var parsed = JsonDocument.Parse(stream);
                var payload = parsed.RootElement;
                if (!payload.TryGetProperty("unit", out var unit) ||
                    !unit.TryGetProperty("path", out var unitPathElement) ||
                    !payload.TryGetProperty("kind", out var kindElement)) return;
                var storedPath = unitPathElement.GetString()?.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrWhiteSpace(kindElement.GetString())) return;
                var absoluteSubject = Path.GetFullPath(Path.Combine(root,
                    storedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathConfinement.IsWithin(root, absoluteSubject)) return;
                PathConfinement.RejectReparseTraversal(root, absoluteSubject);
                var normalized = Path.GetRelativePath(root, absoluteSubject).Replace('\\', '/');
                lock (gate)
                    documents[path] = new IndexedDocument(path, normalized, kindElement.GetString()!, payload.Clone());
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
