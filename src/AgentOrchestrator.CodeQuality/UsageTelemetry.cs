using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record TokenUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs);

public sealed record ReviewerUsage(
    string CliType,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs);

public sealed record ReviewUsageEntry(
    string RunId,
    DateTimeOffset Timestamp,
    string Model,
    string CliType,
    TokenUsage Tokens,
    string Kind,
    string Level,
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null,
    int SchemaVersion = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ObservationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestedModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThinkingLevel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RoutePolicyVersion = null);

public sealed record UsageAggregate(string Key, int Runs, long InputTokens, long OutputTokens,
    long CachedInputTokens, long ReasoningOutputTokens, long DurationMs);

public sealed record UsageReport(
    DateTimeOffset GeneratedAt,
    int Runs,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningOutputTokens,
    long DurationMs,
    IReadOnlyList<UsageAggregate> ByModel,
    IReadOnlyList<UsageAggregate> ByKind,
    IReadOnlyList<UsageAggregate> ByDay,
    IReadOnlyList<UsageAggregate> ByReviewRun,
    IReadOnlyList<ReviewUsageEntry> Recent);

/// <summary>Append-only, repository-local token ledger independent of review metadata rewrites.</summary>
public static class UsageLedger
{
    public const int CurrentSchemaVersion = 3;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "usage", timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task AppendAsync(string repositoryRoot, ReviewUsageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsSupported(entry))
            throw new ArgumentException("Usage ledger entries must conform to schema version 1 or 2.", nameof(entry));
        var path = GetLedgerPath(repositoryRoot, entry.Timestamp);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<UsageReport> QueryAsync(string repositoryRoot, DateTimeOffset? since = null,
        string? kind = null, int recentLimit = 50, CancellationToken cancellationToken = default)
    {
        var entries = new List<ReviewUsageEntry>();
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "usage");
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ReviewUsageEntry>(line, JsonOptions);
                        if (entry is not null && IsSupported(entry) &&
                            (!since.HasValue || entry.Timestamp >= since.Value) &&
                            (string.IsNullOrWhiteSpace(kind) || string.Equals(entry.Kind, kind, StringComparison.Ordinal)))
                            entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // A partial/corrupt historical line must not hide the rest of the append-only ledger.
                    }
                }
            }
        }

        var ordered = entries.OrderByDescending(entry => entry.Timestamp).ToArray();
        return new UsageReport(DateTimeOffset.UtcNow, ordered.Length,
            Sum(ordered, entry => entry.Tokens.InputTokens), Sum(ordered, entry => entry.Tokens.OutputTokens),
            Sum(ordered, entry => entry.Tokens.CachedInputTokens), Sum(ordered, entry => entry.Tokens.ReasoningOutputTokens),
            ordered.Sum(entry => entry.Tokens.DurationMs),
            Aggregate(ordered, entry => entry.Model), Aggregate(ordered, entry => entry.Kind),
            Aggregate(ordered, entry => entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd")),
            Aggregate(ordered, entry => entry.ReviewRunId ?? entry.RunId),
            ordered.Take(Math.Clamp(recentLimit, 1, 200)).ToArray());
    }

    private static long Sum(IEnumerable<ReviewUsageEntry> entries, Func<ReviewUsageEntry, long?> selector) =>
        entries.Sum(entry => selector(entry) ?? 0);

    private static bool IsSupported(ReviewUsageEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RunId) ||
            string.IsNullOrWhiteSpace(entry.Model) ||
            string.IsNullOrWhiteSpace(entry.CliType) ||
            string.IsNullOrWhiteSpace(entry.Kind) ||
            string.IsNullOrWhiteSpace(entry.Level) ||
            string.IsNullOrWhiteSpace(entry.Path) ||
            entry.Tokens is null)
            return false;

        return entry.SchemaVersion switch
        {
            1 => entry.ReviewRunId is null && entry.ObservationId is null,
            2 => !string.IsNullOrWhiteSpace(entry.ReviewRunId) && entry.ObservationId is null,
            CurrentSchemaVersion =>
                !string.IsNullOrWhiteSpace(entry.ReviewRunId) &&
                !string.IsNullOrWhiteSpace(entry.ObservationId) &&
                !string.IsNullOrWhiteSpace(entry.Provider) &&
                !string.IsNullOrWhiteSpace(entry.RequestedModel) &&
                !string.IsNullOrWhiteSpace(entry.EffectiveModel) &&
                !string.IsNullOrWhiteSpace(entry.ThinkingLevel) &&
                !string.IsNullOrWhiteSpace(entry.RoutePolicyVersion),
            _ => false,
        };
    }

    private static IReadOnlyList<UsageAggregate> Aggregate(IEnumerable<ReviewUsageEntry> entries, Func<ReviewUsageEntry, string> key) =>
        entries.GroupBy(key, StringComparer.Ordinal).Select(group => new UsageAggregate(group.Key, group.Count(),
            Sum(group, entry => entry.Tokens.InputTokens), Sum(group, entry => entry.Tokens.OutputTokens),
            Sum(group, entry => entry.Tokens.CachedInputTokens), Sum(group, entry => entry.Tokens.ReasoningOutputTokens),
            group.Sum(entry => entry.Tokens.DurationMs))).OrderByDescending(item => item.Runs).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
}
