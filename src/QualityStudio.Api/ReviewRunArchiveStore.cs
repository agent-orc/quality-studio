using System.Text;
using System.Text.Json;

namespace QualityStudio.Api;

public sealed record StoredRunArchive(
    RunRecord Run,
    IReadOnlyList<RunOperationRecord> Operations,
    IReadOnlyList<RunFindingRecord> Findings,
    IReadOnlyList<RunAttemptRecord> Attempts);

/// <summary>
/// Persists the tracked, append-only run-history archive described in
/// docs/operations/run-persistence/index.html. This is a separate store from
/// <see cref="ReviewRunStore"/>: that store owns the ignored, mutable crash-recovery journal under
/// .quality/runs/; this store owns the Git-tracked product history under .quality/run-history/.
/// Nothing in the live review hot path calls this store yet (see RP-2 in the dossier's slice plan).
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchiveRoot = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string archiveRoot;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        archiveRoot = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeArchiveRoot.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchiveRoot => archiveRoot;

    /// <summary>Create-only. Throws <see cref="IOException"/> if the run was already archived.</summary>
    public void CreateRun(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateRunId(run.RunId);
        var directory = RunDirectoryForMonth(run.CreatedAt, run.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "run.json"), ReviewRunArchiveJson.SerializeRun(run));
    }

    /// <summary>Append-only. The run must already have been created with <see cref="CreateRun"/>.</summary>
    public void AppendOperation(RunOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = ResolveRunDirectory(operation.RunId);
        AppendLine(Path.Combine(directory, "operations.jsonl"), ReviewRunArchiveJson.SerializeOperation(operation));
    }

    /// <summary>Append-only. The run must already have been created with <see cref="CreateRun"/>.</summary>
    public void AppendFinding(RunFindingRecord finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = ResolveRunDirectory(finding.RunId);
        AppendLine(Path.Combine(directory, "findings.jsonl"), ReviewRunArchiveJson.SerializeFinding(finding));
    }

    /// <summary>Create-only per attempt number. Throws <see cref="IOException"/> on a duplicate attempt number.</summary>
    public void CreateAttempt(RunAttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.AttemptNumber < 1)
            throw new ArgumentException("Attempt numbers start at 1.", nameof(attempt));
        var directory = ResolveRunDirectory(attempt.RunId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var path = Path.Combine(attemptsDirectory, $"{attempt.AttemptNumber:D4}.json");
        WriteCreateOnly(path, ReviewRunArchiveJson.SerializeAttempt(attempt));
    }

    public RunRecord LoadRun(string runId)
    {
        var directory = ResolveRunDirectory(runId);
        return ReviewRunArchiveJson.DeserializeRun(File.ReadAllText(Path.Combine(directory, "run.json")));
    }

    public IReadOnlyList<RunOperationRecord> LoadOperations(string runId) =>
        ReadLines(Path.Combine(ResolveRunDirectory(runId), "operations.jsonl"), ReviewRunArchiveJson.DeserializeOperation);

    public IReadOnlyList<RunFindingRecord> LoadFindings(string runId) =>
        ReadLines(Path.Combine(ResolveRunDirectory(runId), "findings.jsonl"), ReviewRunArchiveJson.DeserializeFinding);

    /// <summary>Every readable attempt, ordered by attempt number ascending.</summary>
    public IReadOnlyList<RunAttemptRecord> LoadAttempts(string runId)
    {
        var attemptsDirectory = Path.Combine(ResolveRunDirectory(runId), "attempts");
        if (!Directory.Exists(attemptsDirectory)) return [];
        return Directory.EnumerateFiles(attemptsDirectory, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(path => ReviewRunArchiveJson.DeserializeAttempt(File.ReadAllText(path)))
            .OrderBy(attempt => attempt.AttemptNumber)
            .ToArray();
    }

    public StoredRunArchive Load(string runId) =>
        new(LoadRun(runId), LoadOperations(runId), LoadFindings(runId), LoadAttempts(runId));

    public bool RunExists(string runId)
    {
        ValidateRunId(runId);
        return TryResolveRunDirectory(runId, out _);
    }

    private string RunDirectoryForMonth(DateTimeOffset createdAt, string runId)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(archiveRoot, month, runId);
    }

    private string ResolveRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!TryResolveRunDirectory(runId, out var directory))
            throw new InvalidOperationException(
                $"Archived run '{runId}' was not found under '{archiveRoot}'. Call CreateRun before appending records.");
        return directory;
    }

    private bool TryResolveRunDirectory(string runId, out string directory)
    {
        if (Directory.Exists(archiveRoot))
        {
            foreach (var monthDirectory in Directory.EnumerateDirectories(archiveRoot).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(monthDirectory, runId);
                if (Directory.Exists(candidate))
                {
                    directory = candidate;
                    return true;
                }
            }
        }
        directory = string.Empty;
        return false;
    }

    private static void ValidateRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (runId is "." or ".." ||
            !string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
    }

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static IReadOnlyList<T> ReadLines<T>(string path, Func<string, T> deserialize)
    {
        if (!File.Exists(path)) return [];
        var items = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                items.Add(deserialize(line));
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete; earlier and
                // later appends are unaffected because each append starts on a fresh line.
            }
        }
        return items;
    }

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
