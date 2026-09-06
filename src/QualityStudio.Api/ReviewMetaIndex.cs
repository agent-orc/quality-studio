using System.Collections.Concurrent;
using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>Indexes review sidecars once per repository and keeps the index current from filesystem events.</summary>
public sealed class ReviewMetaIndex : IDisposable
{
    private readonly ConcurrentDictionary<string, RepositoryIndex> repositories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyList<JsonElement> Read(string dataRoot, string repositoryRoot, string relativePath) =>
        Get(dataRoot, repositoryRoot).Read(relativePath);

    public string Find(string dataRoot, string repositoryRoot, string relativePath, string kind) =>
        Get(dataRoot, repositoryRoot).Find(relativePath, kind);

    public void Dispose()
    {
        foreach (var index in repositories.Values) index.Dispose();
        repositories.Clear();
    }

    private RepositoryIndex Get(string dataRoot, string repositoryRoot) => repositories.GetOrAdd(
        Path.GetFullPath(dataRoot), _ => new RepositoryIndex(Path.GetFullPath(dataRoot), Path.GetFullPath(repositoryRoot)));

    private sealed class RepositoryIndex : IDisposable
    {
        private static readonly EnumerationOptions ConfinedEnumeration = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        private readonly object gate = new();
        private readonly string root;
        private readonly string repositoryRoot;
        private readonly Dictionary<string, IndexedDocument> documents =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly FileSystemWatcher watcher;

        public RepositoryIndex(string root, string repositoryRoot)
        {
            this.root = root;
            this.repositoryRoot = repositoryRoot;
            Directory.CreateDirectory(root);
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

        public void Dispose() => watcher.Dispose();

        private void Update(string path)
        {
            if (!IsReviewMetaPath(path) || !File.Exists(path)) return;
            try
            {
                if (!PathConfinement.IsWithin(root, path)) return;
                PathConfinement.RejectReparseTraversal(root, path);
                using var parsed = JsonDocument.Parse(File.ReadAllText(path));
                var payload = parsed.RootElement;
                if (!payload.TryGetProperty("unit", out var unit) ||
                    !unit.TryGetProperty("path", out var unitPathElement) ||
                    !payload.TryGetProperty("kind", out var kindElement)) return;
                var storedPath = unitPathElement.GetString()?.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(storedPath) || string.IsNullOrWhiteSpace(kindElement.GetString())) return;
                var absoluteSubject = Path.GetFullPath(Path.Combine(repositoryRoot,
                    storedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathConfinement.IsWithin(repositoryRoot, absoluteSubject)) return;
                PathConfinement.RejectReparseTraversal(repositoryRoot, absoluteSubject);
                var normalized = Path.GetRelativePath(repositoryRoot, absoluteSubject).Replace('\\', '/');
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
