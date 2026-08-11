using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    string Method,
    long? PromptCharactersBeforeCompaction = null);

public sealed record ReviewRunEconomyEvidence(
    long StaticDurationMs,
    int PreflightCacheHits,
    int FindingCount,
    int ModelCallsPlanned,
    int ModelCallsBlocked,
    int ModelCallsExecuted,
    long? PromptCharactersBeforeCompaction,
    long? PromptCharactersAfterCompaction,
    TokenUsage ActualUsage,
    ReviewEstimateDeviation? EstimateDeviation);

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
    IReadOnlyList<ScopeExclusion>? AggregateExclusions = null,
    ReviewRunEstimate? Estimate = null,
    long? TokenCap = null,
    decimal? CostCap = null,
    bool Force = false,
    string? ThinkingLevel = null);

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
    string? StopReason = null,
    string PreflightState = "queued",
    int PreflightChecks = 0,
    int PreflightUnavailableChecks = 0,
    string? PreflightResultHash = null,
    long? PreflightDurationMs = null,
    int BlockedFiles = 0,
    int PreflightCacheHits = 0,
    int PreflightFindings = 0);

/// <summary>
/// Stable, aggregation-oriented review-run artifact. Route fields use explicit default markers so
/// downstream evidence ingestion never has to infer whether an override was absent.
/// </summary>
public sealed record ReviewRunResult(
    int SchemaVersion,
    string RunId,
    string RepositoryId,
    string Path,
    string Level,
    string Kind,
    string Model,
    string ThinkingLevel,
    string Cli,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int TotalFiles,
    int CompletedFiles,
    int FailedFiles,
    int SkippedFiles,
    int UsageOperations,
    TokenUsage Usage,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    string? StopReason,
    ReviewRunEconomyEvidence? Economy = null);

public sealed record StoredReviewRun(
    ReviewRunManifest Manifest,
    ReviewRunStatus Status,
    IReadOnlyList<ReviewRunFileTransition> Progress,
    PreflightSnapshot? Preflight = null);

/// <summary>Persists the orchestration state for review sweeps inside a repository.</summary>
public sealed class ReviewRunStore
{
    public const string RelativeRunsPath = ".quality/runs";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
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
        WriteResult(manifest, status);
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

    public void WriteResult(ReviewRunManifest manifest, ReviewRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(status);
        if (!string.Equals(manifest.RunId, status.RunId, StringComparison.Ordinal))
            throw new ArgumentException("The run manifest and result status must have the same run id.");
        var result = new ReviewRunResult(
            1,
            manifest.RunId,
            manifest.RepositoryId,
            manifest.Node.Path,
            manifest.Level,
            manifest.Kind,
            manifest.Model ?? "runner-default",
            manifest.ThinkingLevel ?? "model-default",
            manifest.CliType,
            status.State,
            status.CreatedAt,
            status.StartedAt,
            status.FinishedAt,
            status.TotalFiles,
            status.CompletedFiles,
            status.FailedFiles,
            status.SkippedFiles,
            status.UsageOperations,
            status.Usage,
            status.CostSpent,
            status.Currency,
            status.PriceStatus,
            status.StopReason,
            Economy(manifest, status));
        WriteAtomically(Path.Combine(RunDirectory(status.RunId), "result.json"),
            JsonSerializer.Serialize(result, JsonOptions) + Environment.NewLine);
    }

    private static ReviewRunEconomyEvidence Economy(ReviewRunManifest manifest, ReviewRunStatus status)
    {
        var aggregateBlocked = string.Equals(status.AggregateState, "blocked-preflight", StringComparison.Ordinal) ? 1 : 0;
        return new ReviewRunEconomyEvidence(
            status.PreflightDurationMs ?? 0,
            status.PreflightCacheHits,
            status.PreflightFindings,
            manifest.Estimate?.Operations ?? status.TotalFiles + (manifest.Level == "file" ? 0 : 1),
            status.BlockedFiles + aggregateBlocked,
            status.UsageOperations,
            manifest.Estimate?.PromptCharactersBeforeCompaction,
            manifest.Estimate?.PromptCharacters,
            status.Usage,
            null);
    }

    public void WritePreflight(PreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = RunDirectory(snapshot.RunId);
        Directory.CreateDirectory(directory);
        WriteAtomically(
            Path.Combine(directory, "preflight.json"),
            JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine);
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
                var preflightPath = Path.Combine(directory, "preflight.json");
                var preflight = File.Exists(preflightPath) ? ReadRequired<PreflightSnapshot>(preflightPath) : null;
                if (preflight is not null && !string.Equals(preflight.RunId, manifest.RunId, StringComparison.Ordinal))
                    throw new InvalidDataException($"Preflight result disagrees about the run id in '{directory}'.");
                loaded.Add(new StoredReviewRun(manifest, status, ReadProgress(directory, manifest.RunId), preflight));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                loadFailed?.Invoke(directory, exception);
            }
        }
        return loaded;
    }

    public static bool IsTerminal(string state) =>
        state is "done" or "failed" or "cancelled" or "capped" or "blocked-preflight";

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

    private static void WriteAtomically(string destination, string content)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var bytes = Utf8.GetBytes(content);
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
