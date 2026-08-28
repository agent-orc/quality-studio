using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QualityStudio.Api;

/// <summary>
/// Persists the tracked, Git-committed run-history archive: one create-only run record per run,
/// append-only operation and finding observations, and one create-only record per stopped attempt.
/// This is a separate contract from <see cref="ReviewRunStore"/>, which remains the ignored, mutable
/// crash-recovery journal under <c>.quality/runs/</c>. Nothing here is ever rewritten in place.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    /// <summary>Create-only write of a run's immutable identity and plan. Throws if the run already exists.</summary>
    public void CreateRun(RunArchiveRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var directory = RunDirectory(run.RunId, run.CreatedAt);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(run, JsonOptions) + Environment.NewLine);
    }

    public void AppendOperation(RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        AppendLine(Path.Combine(ResolveRunDirectory(operation.RunId), "operations.jsonl"), operation);
    }

    public void AppendFinding(RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        AppendLine(Path.Combine(ResolveRunDirectory(finding.RunId), "findings.jsonl"), finding);
    }

    /// <summary>
    /// Assigns the next create-only attempt number for a run and durably records it. The factory receives
    /// the assigned number so the caller can stamp it onto the record before it is written; if the write
    /// loses a race for that number, the factory is invoked again for the next one.
    /// </summary>
    public RunAttemptRecord CreateAttempt(string runId, Func<int, RunAttemptRecord> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(factory);
        var attemptsDirectory = Path.Combine(ResolveRunDirectory(runId), "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        for (var number = NextAttemptNumber(attemptsDirectory); ; number++)
        {
            var record = factory(number);
            if (record.AttemptNumber != number)
                throw new ArgumentException($"The attempt factory must stamp attempt number {number}.", nameof(factory));
            var path = Path.Combine(attemptsDirectory, $"{number:D4}.json");
            try
            {
                WriteCreateOnly(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
                return record;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Lost a race for this attempt number to a concurrent writer; retry with the next one.
            }
        }
    }

    public StoredRunArchive Load(string runId)
    {
        var directory = ResolveRunDirectory(runId);
        return ReadArchive(directory);
    }

    public IReadOnlyList<StoredRunArchive> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(archiveRoot)) return [];
        string[] monthDirectories;
        try
        {
            monthDirectories = Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadFailed?.Invoke(archiveRoot, exception);
            return [];
        }
        var loaded = new List<StoredRunArchive>();
        foreach (var monthDirectory in monthDirectories)
        {
            string[] runDirectories;
            try
            {
                runDirectories = Directory.EnumerateDirectories(monthDirectory).Order(StringComparer.Ordinal).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                loadFailed?.Invoke(monthDirectory, exception);
                continue;
            }
            foreach (var runDirectory in runDirectories)
            {
                try
                {
                    loaded.Add(ReadArchive(runDirectory));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    loadFailed?.Invoke(runDirectory, exception);
                }
            }
        }
        return loaded;
    }

    private StoredRunArchive ReadArchive(string directory)
    {
        var run = ReadRequired<RunArchiveRecord>(Path.Combine(directory, "run.json"));
        if (!string.Equals(Path.GetFileName(directory), run.RunId, StringComparison.Ordinal))
            throw new InvalidDataException($"Run archive files disagree about the run id in '{directory}'.");
        return new StoredRunArchive(
            run,
            ReadAttempts(directory),
            ReadLines<RunOperationRecord>(Path.Combine(directory, "operations.jsonl")),
            ReadLines<RunFindingRecord>(Path.Combine(directory, "findings.jsonl")));
    }

    private static IReadOnlyList<RunAttemptRecord> ReadAttempts(string runDirectory)
    {
        var attemptsDirectory = Path.Combine(runDirectory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        return Directory.EnumerateFiles(attemptsDirectory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(ReadRequired<RunAttemptRecord>)
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToArray();
    }

    private static int NextAttemptNumber(string attemptsDirectory)
    {
        var highest = 0;
        foreach (var path in Directory.EnumerateFiles(attemptsDirectory, "*.json"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > highest)
                highest = number;
        }
        return highest + 1;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return Path.Combine(archiveRoot, month, runId);
    }

    private string ResolveRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (Directory.Exists(archiveRoot))
        {
            foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(monthDirectory, runId);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException($"No run archive exists for run id '{runId}'.");
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Run archive file is empty: {path}");

    private static IReadOnlyList<T> ReadLines<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
                if (item is not null) items.Add(item);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return items;
    }

    /// <summary>Mirrors <see cref="ReviewRunStore"/>'s partial-line-tolerant append so a crash mid-write never hides earlier records.</summary>
    private static void AppendLine<T>(string path, T record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(JsonSerializer.Serialize(record, LineJsonOptions) + "\n");
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
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
