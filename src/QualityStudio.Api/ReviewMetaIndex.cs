using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record ReviewMetaDiagnostic(string Path, string Reason, DateTimeOffset ObservedAt);

/// <summary>Indexes bounded, schema-valid review sidecars and quarantines invalid repository-owned input.</summary>
public sealed class ReviewMetaIndex : IDisposable
{
    private readonly ReviewContentLimits limits;
    private readonly ConcurrentDictionary<string, RepositoryIndex> repositories =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public ReviewMetaIndex(IOptions<RepositoryOptions> options)
    {
        limits = options.Value.ContentLimits.Validate();
    }

    public IReadOnlyList<JsonElement> Read(string root, string relativePath) => Get(root).Read(relativePath);

    public string Find(string root, string relativePath, string kind) => Get(root).Find(relativePath, kind);

    public IReadOnlyList<ReviewMetaDiagnostic> Diagnostics(string root) => Get(root).Diagnostics();

    public void Dispose()
    {
        foreach (var index in repositories.Values) index.Dispose();
        repositories.Clear();
    }

    private RepositoryIndex Get(string root) => repositories.GetOrAdd(
        Path.GetFullPath(root), path => new RepositoryIndex(path, limits));

    private sealed class RepositoryIndex : IDisposable
    {
        private static readonly EnumerationOptions ConfinedEnumeration = new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        private readonly object gate = new();
        private readonly object updateGate = new();
        private readonly string root;
        private readonly ReviewContentLimits limits;
        private readonly Dictionary<string, IndexedDocument> documents =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly Dictionary<string, ReviewMetaDiagnostic> diagnostics =
            new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private long indexedBytes;
        private readonly FileSystemWatcher watcher;

        public RepositoryIndex(string root, ReviewContentLimits limits)
        {
            this.root = root;
            this.limits = limits;
            Rescan();

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

        private void Rescan()
        {
            var paths = Directory.EnumerateFiles(root, "*.json", ConfinedEnumeration)
                .Where(IsReviewMetaPath).Take(limits.MaxSidecarCount + 1).ToArray();
            foreach (var path in paths.Take(limits.MaxSidecarCount)) Update(path);
            if (paths.Length > limits.MaxSidecarCount)
            {
                diagnostics["$count"] = new ReviewMetaDiagnostic(
                    ".quality/reviews",
                    $"quarantined: sidecar count exceeds {limits.MaxSidecarCount}",
                    DateTimeOffset.UtcNow);
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

        public IReadOnlyList<ReviewMetaDiagnostic> Diagnostics()
        {
            lock (gate)
            {
                return diagnostics.Values.OrderBy(item => item.Path, StringComparer.Ordinal)
                    .Take(100).ToArray();
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
            lock (updateGate) UpdateCore(path);
        }

        private void UpdateCore(string path)
        {
            if (!IsReviewMetaPath(path) || !File.Exists(path)) return;
            try
            {
                if (!PathConfinement.IsWithin(root, path)) return;
                PathConfinement.RejectReparseTraversal(root, path);
                lock (gate)
                {
                    RemoveDocumentLocked(path);
                    diagnostics.Remove(path);
                    if (documents.Count >= limits.MaxSidecarCount)
                        throw new ReviewContentLimitException(
                            $"sidecar count exceeds {limits.MaxSidecarCount}");
                }
                var json = BoundedRepositoryFile.ReadAllText(root, path, limits.MaxSidecarBytes);
                var contract = ReviewMetaJson.Deserialize(json);
                if (contract.Findings.Count > limits.MaxFindings)
                    throw new ReviewContentLimitException($"finding count exceeds {limits.MaxFindings}");
                if (contract.Threads.Count > limits.MaxThreads)
                    throw new ReviewContentLimitException($"thread count exceeds {limits.MaxThreads}");
                using var parsed = JsonDocument.Parse(json);
                ValidateTextFields(parsed.RootElement, limits.MaxTextFieldCharacters);
                var storedBytes = Math.Max(
                    Encoding.UTF8.GetByteCount(json),
                    checked((long)json.Length * sizeof(char)));
                var payload = parsed.RootElement;
                var storedPath = contract.Unit.Path.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(storedPath)) throw new JsonException("Review metadata has no unit path.");
                var absoluteSubject = Path.GetFullPath(Path.Combine(root,
                    storedPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathConfinement.IsWithin(root, absoluteSubject))
                    throw new ArgumentException("Review metadata unit path escapes the repository.");
                PathConfinement.RejectReparseTraversal(root, absoluteSubject);
                var normalized = Path.GetRelativePath(root, absoluteSubject).Replace('\\', '/');
                lock (gate)
                {
                    if (documents.Count >= limits.MaxSidecarCount)
                        throw new ReviewContentLimitException(
                            $"sidecar count exceeds {limits.MaxSidecarCount}");
                    if (indexedBytes + storedBytes > limits.MaxSidecarAggregateBytes)
                        throw new ReviewContentLimitException(
                            $"indexed sidecars exceed {limits.MaxSidecarAggregateBytes} aggregate bytes");
                    documents[path] = new IndexedDocument(path, normalized,
                        contract.Kind.ToString().ToLowerInvariant(), payload.Clone(), storedBytes);
                    indexedBytes += storedBytes;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              ArgumentException or ReviewContentLimitException or InvalidOperationException or
                                              OverflowException)
            {
                lock (gate)
                {
                    RemoveDocumentLocked(path);
                    if (diagnostics.Count < 100 || diagnostics.ContainsKey(path))
                        diagnostics[path] = new ReviewMetaDiagnostic(
                            Path.GetRelativePath(root, path).Replace('\\', '/'),
                            "quarantined: " + BoundedReason(exception),
                            DateTimeOffset.UtcNow);
                }
            }
        }

        private void Remove(string path)
        {
            lock (updateGate)
            {
                lock (gate)
                {
                    RemoveDocumentLocked(path);
                    diagnostics.Remove(path);
                }
            }
        }

        private void RemoveDocumentLocked(string path)
        {
            if (documents.Remove(path, out var removed)) indexedBytes -= removed.StoredBytes;
        }

        private static void ValidateTextFields(JsonElement element, int maximumCharacters)
        {
            if (element.ValueKind == JsonValueKind.String && (element.GetString()?.Length ?? 0) > maximumCharacters)
                throw new ReviewContentLimitException($"text field exceeds {maximumCharacters} characters");
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) ValidateTextFields(property.Value, maximumCharacters);
            if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) ValidateTextFields(item, maximumCharacters);
        }

        private static string BoundedReason(Exception exception) => exception switch
        {
            ReviewContentLimitException => exception.Message,
            JsonException => "schema-invalid review metadata",
            ArgumentException => "unconfined review metadata path",
            _ => "review metadata could not be read safely",
        };

        private static bool IsReviewMetaPath(string path) =>
            path.Contains(".review-meta.", StringComparison.Ordinal) &&
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        private sealed record IndexedDocument(
            string Path, string UnitPath, string Kind, JsonElement Payload, long StoredBytes);
    }
}
