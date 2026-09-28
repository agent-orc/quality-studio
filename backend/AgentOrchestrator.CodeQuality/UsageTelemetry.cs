using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Estimated provider cost of one operation at the catalog price valid at its timestamp.
/// <see cref="Total"/> is null when no price resolved (unknown model, no price for the date), never a
/// silent zero; <see cref="Status"/> names the reason in the price catalog's vocabulary.
/// </summary>
public sealed record UsageCost(decimal? Total, string? Currency, string Status);

/// <summary>
/// Tokens one operation consumed. <see cref="InputTokens"/> is all input the provider billed:
/// fresh input plus <see cref="CachedInputTokens"/> (cache reads) plus
/// <see cref="CacheWriteInputTokens"/> (cache writes), so caps and totals see every input token.
/// <see cref="CacheWriteInputTokens"/> is null when the CLI does not report cache writes.
/// </summary>
public sealed record TokenUsage(
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? ReasoningOutputTokens,
    long DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheWriteInputTokens = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UsageCost? Cost = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PromptCharacters = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PriceAccuracy = null);

/// <summary>Values of <see cref="ReviewUsageEntry.PriceAccuracy"/>, set only when a ledger is read.</summary>
public static class UsagePriceAccuracy
{
    /// <summary>
    /// A Claude operation recorded before schema version 4: the runner dropped
    /// <c>cache_creation_input_tokens</c>, so its tokens and price are a lower bound.
    /// </summary>
    public const string UnderPriced = "underPriced";
}

public sealed record UsageAggregate(string Key, int Runs, long InputTokens, long OutputTokens,
    long CachedInputTokens, long ReasoningOutputTokens, long DurationMs, long CacheWriteInputTokens = 0);

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
    IReadOnlyList<ReviewUsageEntry> Recent,
    decimal? EstimatedCost = null,
    string? CostCurrency = null,
    int UnpricedRuns = 0,
    long CacheWriteInputTokens = 0,
    int UnderPricedRuns = 0);

/// <summary>Append-only, project-local token ledger independent of review metadata rewrites.</summary>
public static class UsageLedger
{
    public const int CurrentSchemaVersion = 4;

    /// <summary>The CLI whose pre-v4 entries lack cache writes and count input without cache reads.</summary>
    private const string ClaudeCliType = "claude";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(GetLedgerDirectory(repositoryRoot), timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    /// <summary>The folder holding every monthly ledger of one project.</summary>
    public static string GetLedgerDirectory(string repositoryRoot) =>
        QualityDataRoot.Combine(repositoryRoot, "usage");

    public static async Task AppendAsync(string repositoryRoot, ReviewUsageEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsSupported(entry))
            throw new ArgumentException("Usage ledger entries must conform to schema version 1, 2, 3, or 4.", nameof(entry));
        if (entry.SchemaVersion < CurrentSchemaVersion &&
            (entry.Tokens.CacheWriteInputTokens is not null || entry.PromptCharacters is not null))
            throw new ArgumentException("Cache-write tokens and prompt size need schema version 4.", nameof(entry));
        // The accuracy flag is derived when a ledger is read and never persisted.
        entry = entry with { PriceAccuracy = null };
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
        var ordered = await ReadAsync(repositoryRoot, since, kind, cancellationToken).ConfigureAwait(false);
        var costs = ordered.Select(entry => entry.Cost!).ToArray();
        var priced = costs.Where(cost => cost.Total.HasValue).ToArray();
        return new UsageReport(DateTimeOffset.UtcNow, ordered.Count,
            Sum(ordered, entry => entry.Tokens.InputTokens), Sum(ordered, entry => entry.Tokens.OutputTokens),
            Sum(ordered, entry => entry.Tokens.CachedInputTokens), Sum(ordered, entry => entry.Tokens.ReasoningOutputTokens),
            ordered.Sum(entry => entry.Tokens.DurationMs),
            Aggregate(ordered, entry => entry.Model), Aggregate(ordered, entry => entry.Kind),
            Aggregate(ordered, entry => entry.Timestamp.UtcDateTime.ToString("yyyy-MM-dd")),
            Aggregate(ordered, entry => entry.ReviewRunId ?? entry.RunId),
            ordered.Take(Math.Clamp(recentLimit, 1, 200)).ToArray(),
            priced.Length == 0 ? null : priced.Sum(cost => cost.Total!.Value),
            priced.FirstOrDefault()?.Currency,
            costs.Length - priced.Length,
            Sum(ordered, entry => entry.Tokens.CacheWriteInputTokens),
            ordered.Count(entry => entry.PriceAccuracy == UsagePriceAccuracy.UnderPriced));
    }

    /// <summary>
    /// Reads every supported ledger entry, newest first, in the current token vocabulary and with
    /// its cost: stored, or priced at read time for entries written before costs were recorded.
    /// Pre-v4 Claude entries are normalised and flagged <see cref="UsagePriceAccuracy.UnderPriced"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ReviewUsageEntry>> ReadAsync(string repositoryRoot,
        DateTimeOffset? since = null, string? kind = null, CancellationToken cancellationToken = default)
    {
        var entries = new List<ReviewUsageEntry>();
        var directory = GetLedgerDirectory(repositoryRoot);
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
                            entries.Add(Normalize(entry));
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

    /// <summary>
    /// Brings a stored entry into the current vocabulary without rewriting the ledger. Before
    /// version 4 the runner reported Claude's <c>input_tokens</c>, which excludes cache reads and
    /// writes, and dropped <c>cache_creation_input_tokens</c>; the stored cost then subtracted the
    /// cache reads from that fresh input a second time. Such an entry counts its cache reads as
    /// input again and is re-priced, but its cache writes are gone, so it stays a lower bound and
    /// is flagged. Entries written before costs were recorded are priced here.
    /// </summary>
    internal static ReviewUsageEntry Normalize(ReviewUsageEntry entry)
    {
        if (entry.SchemaVersion < CurrentSchemaVersion &&
            string.Equals(entry.CliType, ClaudeCliType, StringComparison.OrdinalIgnoreCase))
        {
            var tokens = entry.Tokens.InputTokens is null
                ? entry.Tokens
                : entry.Tokens with { InputTokens = entry.Tokens.InputTokens + Math.Max(0, entry.Tokens.CachedInputTokens ?? 0) };
            return entry with
            {
                Tokens = tokens,
                Cost = EstimateCost(entry.Model, tokens, entry.Timestamp),
                PriceAccuracy = UsagePriceAccuracy.UnderPriced,
            };
        }
        return entry with { Cost = entry.Cost ?? EstimateCost(entry.Model, entry.Tokens, entry.Timestamp) };
    }

    /// <summary>
    /// Prices one operation at the catalog price valid at <paramref name="timestamp"/>. The result
    /// is stored with the ledger entry so every cost the product incurs is visible where the tokens
    /// are, and recomputed at query time for entries written before costs were recorded.
    /// </summary>
    public static UsageCost EstimateCost(string model, TokenUsage tokens, DateTimeOffset timestamp)
    {
        var cost = ReviewPriceCatalog.Default.ComputeCost(model, ToPricingUsage(tokens), timestamp.UtcDateTime);
        var status = cost.Status.ToString();
        return new UsageCost(cost.Total, cost.Currency, char.ToLowerInvariant(status[0]) + status[1..]);
    }

    /// <summary>
    /// Splits recorded usage into the catalogue's four tariffs: fresh input, cache reads, cache
    /// writes, and output. Cache reads and writes are part of <see cref="TokenUsage.InputTokens"/>,
    /// so each is bounded by what is left of it and the remainder is fresh input.
    /// </summary>
    public static PricingTokenUsage ToPricingUsage(TokenUsage tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var input = Math.Max(0, tokens.InputTokens ?? 0);
        var cacheRead = Math.Clamp(tokens.CachedInputTokens ?? 0, 0, input);
        var cacheWrite = Math.Clamp(tokens.CacheWriteInputTokens ?? 0, 0, input - cacheRead);
        return new PricingTokenUsage(input - cacheRead - cacheWrite, Math.Max(0, tokens.OutputTokens ?? 0),
            cacheRead, cacheWrite);
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
            1 => entry.ReviewRunId is null,
            2 => !string.IsNullOrWhiteSpace(entry.ReviewRunId),
            // v3 attributes every operation to a model source; the sweep id is optional because
            // standalone CLI reviews have none. v4 adds cache writes and the prompt size.
            3 or CurrentSchemaVersion => !string.IsNullOrWhiteSpace(entry.ModelSource),
            _ => false,
        };
    }

    private static IReadOnlyList<UsageAggregate> Aggregate(IEnumerable<ReviewUsageEntry> entries, Func<ReviewUsageEntry, string> key) =>
        entries.GroupBy(key, StringComparer.Ordinal).Select(group => new UsageAggregate(group.Key, group.Count(),
            Sum(group, entry => entry.Tokens.InputTokens), Sum(group, entry => entry.Tokens.OutputTokens),
            Sum(group, entry => entry.Tokens.CachedInputTokens), Sum(group, entry => entry.Tokens.ReasoningOutputTokens),
            group.Sum(entry => entry.Tokens.DurationMs), Sum(group, entry => entry.Tokens.CacheWriteInputTokens)))
            .OrderByDescending(item => item.Runs).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
}
