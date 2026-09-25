using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelPriceCatalog = CodingAgentRunner.Pricing.ModelPriceCatalog;
using PricingTokenUsage = CodingAgentRunner.Pricing.TokenUsage;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Estimated provider cost of one operation at the catalog price valid at its timestamp.
/// <see cref="Total"/> is null when no price resolved (unknown model, no price for the date), never a
/// silent zero; <see cref="Status"/> names the reason in the price catalog's vocabulary.
/// </summary>
public sealed record UsageCost(decimal? Total, string? Currency, string Status);

/// <summary>
/// Token counts of one operation. <see cref="InputTokens"/> is every input token the model
/// processed, including cache reads and cache writes, so caps and totals count all input;
/// <see cref="CachedInputTokens"/> (cache reads) and <see cref="CacheWriteInputTokens"/> (cache
/// writes, Claude's <c>cache_creation_input_tokens</c>) are subsets of it that the price catalogue
/// prices at their own rates. Ledger entries before schema version 4 recorded Claude's fresh input
/// only and no cache writes; <see cref="UsageLedger"/> normalizes them on read.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AccountingNote = null);

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

    /// <summary>
    /// Set on entries read from before schema version 4 that ran on Claude: the runner dropped
    /// <c>cache_creation_input_tokens</c>, so their cost is a lower bound (the 2026-09-28 evaluation
    /// showed USD 12.50 recorded against USD 22.53 billed).
    /// </summary>
    public const string UnderPricedNote =
        "Recorded before ledger schema 4: Claude cache-write tokens were not captured, so this cost is a lower bound.";
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
            throw new ArgumentException(
                $"Usage ledger entries must conform to a supported schema version (1 to {CurrentSchemaVersion}).", nameof(entry));
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
        var entries = await ReadAsync(repositoryRoot, since, kind, cancellationToken).ConfigureAwait(false);
        var ordered = entries.OrderByDescending(entry => entry.Timestamp).ToArray();
        var costs = ordered.Select(entry => entry.Cost!).ToArray();
        var priced = costs.Where(cost => cost.Total.HasValue).ToArray();
        return new UsageReport(DateTimeOffset.UtcNow, ordered.Length,
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
            ordered.Count(IsUnderPriced));
    }

    /// <summary>
    /// Reads every supported entry, normalized to the schema 4 token semantics and priced: entries
    /// written before costs were recorded, entries whose stored cost was unknownModel, and
    /// pre-schema-4 Claude entries whose stored cost mispriced cache reads are priced at read
    /// time; the latter carry <see cref="UnderPricedNote"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ReviewUsageEntry>> ReadAsync(string repositoryRoot,
        DateTimeOffset? since = null, string? kind = null, CancellationToken cancellationToken = default)
    {
        var entries = new List<ReviewUsageEntry>();
        var directory = GetLedgerDirectory(repositoryRoot);
        if (!Directory.Exists(directory)) return entries;
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
        return entries;
    }

    /// <summary>
    /// True for an entry whose recorded usage is known to miss billed tokens: a Claude operation
    /// written before schema version 4, when cache-write tokens were not captured.
    /// </summary>
    public static bool IsUnderPriced(ReviewUsageEntry entry) =>
        entry.SchemaVersion < 4 && IsClaude(entry.CliType);

    /// <summary>
    /// Brings an entry to the schema 4 token semantics without rewriting the append-only file. Before
    /// schema 4 Claude's input count was fresh input only (cache reads were reported beside it, cache
    /// writes not at all), so cache reads are added to the input and the cost is recomputed.
    /// </summary>
    internal static ReviewUsageEntry Normalize(ReviewUsageEntry entry)
    {
        if (IsUnderPriced(entry))
        {
            var tokens = entry.Tokens.InputTokens.HasValue
                ? entry.Tokens with
                {
                    InputTokens = Math.Max(0, entry.Tokens.InputTokens.Value) + Math.Max(0, entry.Tokens.CachedInputTokens ?? 0),
                }
                : entry.Tokens;
            return entry with
            {
                Tokens = tokens,
                Cost = EstimateCost(entry.Model, tokens, entry.Timestamp),
                AccountingNote = UnderPricedNote,
            };
        }
        return entry.Cost is null or { Status: "unknownModel" }
            ? entry with { Cost = EstimateCost(entry.Model, entry.Tokens, entry.Timestamp) }
            : entry;
    }

    /// <summary>
    /// Prices one operation at the catalog price valid at <paramref name="timestamp"/>. The result
    /// is stored with the ledger entry so every cost the product incurs is visible where the tokens
    /// are, and recomputed at query time for entries written before costs were recorded.
    /// </summary>
    public static UsageCost EstimateCost(string model, TokenUsage tokens, DateTimeOffset timestamp,
        ModelPriceCatalog? catalog = null)
    {
        var cost = (catalog ?? ReviewPriceCatalog.Default).ComputeCost(model, ToPricingUsage(tokens), timestamp.UtcDateTime);
        var status = cost.Status.ToString();
        return new UsageCost(cost.Total, cost.Currency, char.ToLowerInvariant(status[0]) + status[1..]);
    }

    /// <summary>
    /// Splits the all-input count into the catalogue's price classes: fresh input, cache reads and
    /// cache writes, each priced at its own rate.
    /// </summary>
    public static PricingTokenUsage ToPricingUsage(TokenUsage tokens)
    {
        var input = Math.Max(0, tokens.InputTokens ?? 0);
        var cacheRead = Math.Clamp(tokens.CachedInputTokens ?? 0, 0, input);
        var cacheWrite = Math.Clamp(tokens.CacheWriteInputTokens ?? 0, 0, input - cacheRead);
        return new PricingTokenUsage(input - cacheRead - cacheWrite, Math.Max(0, tokens.OutputTokens ?? 0),
            cacheRead, cacheWrite);
    }

    internal static bool IsClaude(string? cliType) =>
        string.Equals(cliType, "claude", StringComparison.OrdinalIgnoreCase);

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
            // standalone CLI reviews have none. v4 keeps that shape and counts all input, including
            // cache reads and writes, in inputTokens.
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
