using System.Collections.Concurrent;
using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>Indexes review sidecars once per repository and keeps the index current from filesystem events.</summary>
public sealed class ReviewMetaIndex : IDisposable
{
    private readonly ConcurrentDictionary<string, RepositoryIndex> repositories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyList<JsonElement> Read(string root, string dataRoot, string relativePath) =>
        Get(root, dataRoot).Read(relativePath);

    public string Find(string root, string dataRoot, string relativePath, string kind) =>
        Get(root, dataRoot).Find(relativePath, kind);

    public void Dispose()
    {
        foreach (var index in repositories.Values) index.Dispose();
        repositories.Clear();
    }

    private RepositoryIndex Get(string root, string dataRoot)
    {
        var key = Path.GetFullPath(root) + "\0" + Path.GetFullPath(dataRoot);
        return repositories.GetOrAdd(key, _ => new RepositoryIndex(root, dataRoot));
    }

    private sealed class RepositoryIndex : IDisposable
    {
        private static readonly EnumerationOptions ConfinedEnumeration = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        private readonly object gate = new();
        private readonly string root;
        private readonly string dataRoot;
        private readonly Dictionary<string, IndexedDocument> documents =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly FileSystemWatcher watcher;

        public RepositoryIndex(string root, string dataRoot)
        {
            this.root = Path.GetFullPath(root);
            this.dataRoot = Path.GetFullPath(dataRoot);
            Directory.CreateDirectory(this.dataRoot);
            foreach (var path in Directory.EnumerateFiles(this.dataRoot, "*.json", ConfinedEnumeration)
                         .Where(IsReviewMetaPath))
                Update(path);

            watcher = new FileSystemWatcher(this.dataRoot, "*.json")
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

        public void Dispose() => watcher.Dispose();

        private void Update(string path)
        {
            if (!IsReviewMetaPath(path) || !File.Exists(path)) return;
            try
            {
                if (!PathConfinement.IsWithin(dataRoot, path)) return;
                PathConfinement.RejectReparseTraversal(dataRoot, path);
                using var parsed = JsonDocument.Parse(File.ReadAllText(path));
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
            lock (gate) documents.Remove(path);
        }

        private static bool IsReviewMetaPath(string path) =>
            path.Contains(".review-meta.", StringComparison.Ordinal) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private sealed record IndexedDocument(string Path, string UnitPath, string Kind, JsonElement Payload);
    }
}
