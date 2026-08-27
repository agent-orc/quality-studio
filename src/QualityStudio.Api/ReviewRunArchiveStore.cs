using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>
/// Persists the tracked, product-owned review run history described in the run-persistence dossier:
/// one immutable manifest, append-only operation and finding observations, and create-only attempt
/// snapshots under <c>.quality/run-history/YYYY-MM/&lt;runId&gt;/</c>. This store never touches the
/// mutable, ignored recovery journal owned by <see cref="ReviewRunStore"/>, and it never performs any
/// Git operation.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string archiveRoot;
    private readonly ConcurrentDictionary<string, string> knownRunDirectories = new(StringComparer.Ordinal);

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    /// <summary>Create-only write of the immutable run manifest. Throws if the run already exists.</summary>
    public string CreateRun(RunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var directory = RunDirectory(MonthOf(run.CreatedAt), run.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), Serialize(run));
        knownRunDirectories[run.RunId] = directory;
        return directory;
    }

    /// <summary>Appends one completed or failed operation observation. The run must already be archived.</summary>
    public void AppendOperation(string runId, RunArchiveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        AppendLine(RequireRunDirectory(runId), "operations.jsonl", SerializeLine(operation));
    }

    /// <summary>Appends one finding observation for an already-archived operation.</summary>
    public void AppendFinding(string runId, RunArchiveFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        AppendLine(RequireRunDirectory(runId), "findings.jsonl", SerializeLine(finding));
    }

    /// <summary>
    /// Writes the next create-only attempt snapshot for a run. <paramref name="attemptFactory"/> receives
    /// the next attempt ordinal (starting at 1) so the caller can stamp it into the record it builds.
    /// </summary>
    public RunArchiveAttempt WriteNextAttempt(string runId, Func<int, RunArchiveAttempt> attemptFactory)
    {
        ArgumentNullException.ThrowIfNull(attemptFactory);
        var directory = RequireRunDirectory(runId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var next = NextAttemptNumber(attemptsDirectory);
        var attempt = attemptFactory(next);
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt != next)
            throw new ArgumentException(
                $"Attempt factory produced attempt {attempt.Attempt} but the next create-only attempt for '{runId}' is {next}.");
        if (!string.Equals(attempt.RunId, runId, StringComparison.Ordinal))
            throw new ArgumentException("An attempt record must reference the same run id as the archived run.", nameof(attemptFactory));
        WriteCreateOnly(Path.Combine(attemptsDirectory, $"{next:D4}.json"), Serialize(attempt));
        return attempt;
    }

    public StoredRunArchive? TryLoad(string runId, Action<string, Exception>? loadFailed = null)
    {
        var directory = FindRunDirectory(runId);
        return directory is null ? null : LoadDirectory(directory, loadFailed);
    }

    public IReadOnlyList<StoredRunArchive> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(archiveRoot)) return [];
        var results = new List<StoredRunArchive>();
        foreach (var monthDirectory in SafeEnumerateDirectories(archiveRoot, loadFailed))
        {
            if (!IsMonthDirectoryName(Path.GetFileName(monthDirectory))) continue;
            foreach (var runDirectory in SafeEnumerateDirectories(monthDirectory, loadFailed))
            {
                var loaded = LoadDirectory(runDirectory, loadFailed);
                if (loaded is not null) results.Add(loaded);
            }
        }
        return results.OrderBy(archive => archive.Run.RunId, StringComparer.Ordinal).ToArray();
    }

    private string RequireRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new InvalidOperationException(
            $"Archived review run '{runId}' was not found under '{archiveRoot}'. Create it before appending to it.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (knownRunDirectories.TryGetValue(runId, out var cached) && Directory.Exists(cached)) return cached;
        if (!Directory.Exists(archiveRoot)) return null;
        foreach (var monthDirectory in SafeEnumerateDirectories(archiveRoot, null))
        {
            if (!IsMonthDirectoryName(Path.GetFileName(monthDirectory))) continue;
            var candidate = Path.Combine(monthDirectory, runId);
            if (!Directory.Exists(candidate)) continue;
            PathConfinement.RejectReparseTraversal(archiveRoot, candidate);
            knownRunDirectories[runId] = candidate;
            return candidate;
        }
        return null;
    }

    private StoredRunArchive? LoadDirectory(string directory, Action<string, Exception>? loadFailed)
    {
        try
        {
            var run = ReadRequired<RunArchiveRecord>(Path.Combine(directory, "run.json"));
            if (!string.Equals(Path.GetFileName(directory), run.RunId, StringComparison.Ordinal))
                throw new InvalidDataException($"Archived run files disagree about the run id in '{directory}'.");
            var operations = ReadLines<RunArchiveOperation>(Path.Combine(directory, "operations.jsonl"));
            var findings = ReadLines<RunArchiveFinding>(Path.Combine(directory, "findings.jsonl"));
            var attempts = ReadAttempts(Path.Combine(directory, "attempts"));
            knownRunDirectories[run.RunId] = directory;
            return new StoredRunArchive(run, operations, findings, attempts);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            loadFailed?.Invoke(directory, exception);
            return null;
        }
    }

    private IReadOnlyList<RunArchiveAttempt> ReadAttempts(string attemptsDirectory)
    {
        if (!Directory.Exists(attemptsDirectory)) return [];
        var attempts = new List<RunArchiveAttempt>();
        foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            attempts.Add(ReadRequired<RunArchiveAttempt>(file));
        }
        return attempts;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, Action<string, Exception>? loadFailed)
    {
        try
        {
            return Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadFailed?.Invoke(root, exception);
            return [];
        }
    }

    private string RunDirectory(string month, string runId)
    {
        ValidateRunId(runId);
        if (!IsMonthDirectoryName(month))
            throw new ArgumentException($"'{month}' is not a valid archive month segment (expected yyyy-MM).", nameof(month));
        var directory = Path.Combine(archiveRoot, month, runId);
        PathConfinement.RejectReparseTraversal(archiveRoot, directory);
        return directory;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            runId is "." or "..")
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static bool IsMonthDirectoryName(string name) =>
        name.Length == 7 && name[4] == '-' &&
        int.TryParse(name.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
        int.TryParse(name.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
        month is >= 1 and <= 12;

    private static string MonthOf(DateTimeOffset createdAt) =>
        createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static void AppendLine(string directory, string fileName, string line)
    {
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        var bytes = Utf8.GetBytes(line + "\n");
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            bufferSize: 4096, FileOptions.WriteThrough);
        if (stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() != '\n')
            {
                stream.Position = stream.Length;
                stream.WriteByte((byte)'\n');
            }
        }
        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), RunArchiveJson.Options)
        ?? throw new InvalidDataException($"Archived review run file is empty: {path}");

    private static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<T>(line, RunArchiveJson.LineOptions);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Preserve every
                // earlier and later readable record, matching the existing progress.jsonl behavior.
            }
        }
        return records;
    }

    private static int NextAttemptNumber(string attemptsDirectory)
    {
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                number > highest)
                highest = number;
        }
        return highest + 1;
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, RunArchiveJson.Options) + Environment.NewLine;

    private static string SerializeLine<T>(T value) =>
        JsonSerializer.Serialize(value, RunArchiveJson.LineOptions);

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
