using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewRunPlanNode(string Id, string Name, string Path);

public sealed record ReviewRunPlanTarget(string Id, string Name, string Path, string SubjectHash);

public sealed record ReviewRunEstimate(
    int Files,
    int Operations,
    long PromptCharacters,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    string? Currency,
    string PriceStatus,
    int HistorySamples,
    string Method);

public sealed record ReviewRunManifest(
    string RunId,
    string RepositoryId,
    ReviewRunPlanNode Node,
    string Level,
    string Kind,
    string? Model,
    string CliType,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    IReadOnlyList<string>? AggregateControls,
    ReviewRunEstimate? Estimate = null,
    long? TokenCap = null,
    decimal? CostCap = null);

public sealed record ReviewRunFileTransition(
    string Path,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string RunId,
    string? Error);

public sealed record ReviewRunStatus(
    string RunId,
    string State,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int Cursor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<string> Errors,
    int UsageOperations,
    TokenUsage Usage,
    long? TokenCap = null,
    decimal? CostCap = null,
    decimal? CostSpent = null,
    string? Currency = null,
    string PriceStatus = "unknownModel",
    int SkippedFiles = 0,
    string? AggregateState = null,
    string? StopReason = null);

public sealed record StoredReviewRun(
    ReviewRunManifest Manifest,
    ReviewRunStatus Status,
    IReadOnlyList<ReviewRunFileTransition> Progress);

/// <summary>Persists the orchestration state for review sweeps inside a repository.</summary>
public sealed class ReviewRunStore
{
    public const string RelativeRunsPath = ".quality/runs";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LineJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string runsPath;

    public ReviewRunStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        runsPath = Path.Combine(Path.GetFullPath(repositoryRoot), RelativeRunsPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string RunsPath => runsPath;

    public void Create(ReviewRunManifest manifest, ReviewRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(status);
        if (!string.Equals(manifest.RunId, status.RunId, StringComparison.Ordinal))
            throw new ArgumentException("The run manifest and status must have the same run id.");

        var directory = RunDirectory(manifest.RunId);
        Directory.CreateDirectory(directory);
        WriteCreateOnly(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        foreach (var target in manifest.Targets)
        {
            AppendProgress(new ReviewRunFileTransition(target.Path, "queued", null, null, manifest.RunId, null));
        }
        WriteStatus(status);
    }

    public void AppendProgress(ReviewRunFileTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var path = Path.Combine(RunDirectory(transition.RunId), "progress.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = Utf8.GetBytes(JsonSerializer.Serialize(transition, LineJsonOptions) + "\n");
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

    public void WriteStatus(ReviewRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var directory = RunDirectory(status.RunId);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "status.json");
        var temporary = Path.Combine(directory, $"status.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(status, JsonOptions) + Environment.NewLine);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<StoredReviewRun> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(runsPath)) return [];
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(runsPath).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadFailed?.Invoke(runsPath, exception);
            return [];
        }
        var loaded = new List<StoredReviewRun>();
        foreach (var directory in directories)
        {
            try
            {
                var manifest = ReadRequired<ReviewRunManifest>(Path.Combine(directory, "manifest.json"));
                var status = ReadRequired<ReviewRunStatus>(Path.Combine(directory, "status.json"));
                if (!string.Equals(manifest.RunId, status.RunId, StringComparison.Ordinal) ||
                    !string.Equals(Path.GetFileName(directory), manifest.RunId, StringComparison.Ordinal))
                    throw new InvalidDataException($"Review run files disagree about the run id in '{directory}'.");
                loaded.Add(new StoredReviewRun(manifest, status, ReadProgress(directory, manifest.RunId)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                loadFailed?.Invoke(directory, exception);
            }
        }
        return loaded;
    }

    public static bool IsTerminal(string state) => state is "done" or "failed" or "cancelled" or "capped";

    private IReadOnlyList<ReviewRunFileTransition> ReadProgress(string directory, string runId)
    {
        var path = Path.Combine(directory, "progress.jsonl");
        if (!File.Exists(path)) return [];
        var transitions = new List<ReviewRunFileTransition>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var transition = JsonSerializer.Deserialize<ReviewRunFileTransition>(line, LineJsonOptions);
                if (transition is not null && string.Equals(transition.RunId, runId, StringComparison.Ordinal))
                    transitions.Add(transition);
            }
            catch (JsonException)
            {
                // A process crash can leave only the final JSONL record incomplete. Ignore it;
                // later appends start on a fresh line so all preceding and following records survive.
            }
        }
        return transitions;
    }

    private string RunDirectory(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!string.Equals(runId, Path.GetFileName(runId), StringComparison.Ordinal) ||
            runId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new ArgumentException("A review run id cannot contain path separators.", nameof(runId));
        return Path.Combine(runsPath, runId);
    }

    private static T ReadRequired<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Review run file is empty: {path}");

    private static void WriteCreateOnly(string path, string content)
    {
        var bytes = Utf8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
