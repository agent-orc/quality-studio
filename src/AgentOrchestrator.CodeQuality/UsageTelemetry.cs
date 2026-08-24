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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Attempt = null);

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
            throw new ArgumentException("Usage ledger entries must conform to schema version 1, 2, or 3.", nameof(entry));
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
        var entries = await ReadEntriesAsync(repositoryRoot, since, kind, cancellationToken).ConfigureAwait(false);
        return new UsageReport(DateTimeOffset.UtcNow, entries.Count,
            Sum(entries, entry => entry.Tokens.InputTokens), Sum(entries, entry => entry.Tokens.OutputTokens),
            Sum(entries, entry => entry.Tokens.CachedInputTokens), Sum(entries, entry => entry.Tokens.ReasoningOutputTokens),
            entries.Sum(entry => entry.Tokens.DurationMs),
            Aggregate(entries, entry => entry.Model), Aggregate(entries, entry => entry.Kind),
            Aggregate(entries, entry => entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd")),
            Aggregate(entries, entry => entry.ReviewRunId ?? entry.RunId),
            entries.Take(Math.Clamp(recentLimit, 1, 200)).ToArray());
    }

    public static async Task<IReadOnlyList<ReviewUsageEntry>> ReadEntriesAsync(
        string repositoryRoot,
        DateTimeOffset? since = null,
        string? kind = null,
        CancellationToken cancellationToken = default)
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
        return entries.OrderByDescending(entry => entry.Timestamp).ToArray();
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
            1 => entry.ReviewRunId is null && entry.OperationId is null && entry.Attempt is null,
            2 => !string.IsNullOrWhiteSpace(entry.ReviewRunId) && entry.OperationId is null && entry.Attempt is null,
            CurrentSchemaVersion => !string.IsNullOrWhiteSpace(entry.ReviewRunId) &&
                                    !string.IsNullOrWhiteSpace(entry.OperationId) && entry.Attempt > 0,
            _ => false,
        };
    }

    private static IReadOnlyList<UsageAggregate> Aggregate(IEnumerable<ReviewUsageEntry> entries, Func<ReviewUsageEntry, string> key) =>
        entries.GroupBy(key, StringComparer.Ordinal).Select(group => new UsageAggregate(group.Key, group.Count(),
            Sum(group, entry => entry.Tokens.InputTokens), Sum(group, entry => entry.Tokens.OutputTokens),
            Sum(group, entry => entry.Tokens.CachedInputTokens), Sum(group, entry => entry.Tokens.ReasoningOutputTokens),
            group.Sum(entry => entry.Tokens.DurationMs))).OrderByDescending(item => item.Runs).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
}
