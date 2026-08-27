using System.Text;
using System.Text.Json;

namespace QualityStudio.Api;

/// <summary>
/// Repository-owned, tracked history archive for stopped review runs. This store never mutates
/// files written by <see cref="ReviewRunStore"/>: that store remains the ignored, mutable recovery
/// journal under <c>.quality/runs/</c>. This store adds an immutable, git-trackable projection under
/// <c>.quality/run-history/YYYY-MM/&lt;runId&gt;/</c> so a run's identity, operations, finding
/// observations and stopped-attempt history survive loss of the recovery journal or checkout.
/// </summary>
public sealed class ReviewRunArchiveStore
{
    public const string RelativeArchivePath = ".quality/run-history";
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string repositoryRoot;
    private readonly string archivePath;

    public ReviewRunArchiveStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        archivePath = Path.Combine(this.repositoryRoot, RelativeArchivePath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string ArchivePath => archivePath;

    /// <summary>Creates the immutable run record. Throws <see cref="IOException"/> if it already exists.</summary>
    public void CreateRun(ArchivedRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var directory = RunDirectory(record.RunId, record.CreatedAt);
        Directory.CreateDirectory(directory);
        var stamped = record with { Schema = ReviewRunArchiveJson.RunRecordSchemaId, SchemaVersion = 1 };
        WriteCreateOnly(Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.Options) + Environment.NewLine);
    }

    public void AppendOperation(ArchivedRunOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var directory = RequireRunDirectory(operation.RunId);
        var stamped = operation with { Schema = ReviewRunArchiveJson.RunOperationSchemaId, SchemaVersion = 1 };
        AppendLine(Path.Combine(directory, "operations.jsonl"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.LineOptions));
    }

    public void AppendFinding(ArchivedRunFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var directory = RequireRunDirectory(finding.RunId);
        var stamped = finding with { Schema = ReviewRunArchiveJson.RunFindingSchemaId, SchemaVersion = 1 };
        AppendLine(Path.Combine(directory, "findings.jsonl"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.LineOptions));
    }

    /// <summary>Returns the next create-only attempt ordinal for a run (1 for the first stopped attempt).</summary>
    public int NextAttemptOrdinal(string runId)
    {
        var directory = RequireRunDirectory(runId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsDirectory)) return 1;
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name, out var ordinal) && ordinal > highest) highest = ordinal;
        }
        return highest + 1;
    }

    /// <summary>Creates one immutable stopped-attempt snapshot. Throws <see cref="IOException"/> if the ordinal already exists.</summary>
    public void WriteAttempt(ArchivedRunAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Attempt < 1)
            throw new ArgumentException("An archived attempt ordinal must be at least 1.", nameof(attempt));
        var directory = RequireRunDirectory(attempt.RunId);
        var attemptsDirectory = Path.Combine(directory, "attempts");
        Directory.CreateDirectory(attemptsDirectory);
        var stamped = attempt with { Schema = ReviewRunArchiveJson.RunAttemptSchemaId, SchemaVersion = 1 };
        WriteCreateOnly(Path.Combine(attemptsDirectory, $"{attempt.Attempt:D4}.json"),
            JsonSerializer.Serialize(stamped, ReviewRunArchiveJson.Options) + Environment.NewLine);
    }

    /// <summary>Reads back a fully archived run, or <c>null</c> if it has not been archived.</summary>
    public ArchivedReviewRun? LoadRun(string runId)
    {
        var directory = FindRunDirectory(runId);
        if (directory is null) return null;

        var recordPath = Path.Combine(directory, "run.json");
        var record = JsonSerializer.Deserialize<ArchivedRunRecord>(File.ReadAllText(recordPath), ReviewRunArchiveJson.Options)
            ?? throw new InvalidDataException($"Archived run record is empty: {recordPath}");

        var attempts = new List<ArchivedRunAttempt>();
        var attemptsDirectory = Path.Combine(directory, "attempts");
        if (Directory.Exists(attemptsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(attemptsDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                var attempt = JsonSerializer.Deserialize<ArchivedRunAttempt>(File.ReadAllText(file), ReviewRunArchiveJson.Options)
                    ?? throw new InvalidDataException($"Archived run attempt is empty: {file}");
                attempts.Add(attempt);
            }
        }

        return new ArchivedReviewRun(
            record,
            attempts.OrderBy(attempt => attempt.Attempt).ToArray(),
            ReadLines<ArchivedRunOperation>(Path.Combine(directory, "operations.jsonl")),
            ReadLines<ArchivedRunFinding>(Path.Combine(directory, "findings.jsonl")));
    }

    private string RequireRunDirectory(string runId) =>
        FindRunDirectory(runId) ?? throw new InvalidOperationException(
            $"Review run '{runId}' has not been archived. Call {nameof(CreateRun)} first.");

    private string? FindRunDirectory(string runId)
    {
        ValidateRunId(runId);
        if (!Directory.Exists(archivePath)) return null;
        foreach (var monthDirectory in Directory.EnumerateDirectories(archivePath).Order(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(monthDirectory, runId);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "run.json")))
                return candidate;
        }
        return null;
    }

    private string RunDirectory(string runId, DateTimeOffset createdAt)
    {
        ValidateRunId(runId);
        var month = createdAt.UtcDateTime.ToString("yyyy-MM");
        var directory = Path.Combine(archivePath, month, runId);
        PathConfinement.RejectReparseTraversal(repositoryRoot, Path.Combine(archivePath, month));
        if (!PathConfinement.IsWithin(archivePath, directory))
            throw new ArgumentException("The archived run directory escapes the run-history root.", nameof(runId));
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

    private static IReadOnlyList<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var records = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<T>(line, ReviewRunArchiveJson.LineOptions);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return records;
    }

    private static void AppendLine(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(json + "\n");
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
