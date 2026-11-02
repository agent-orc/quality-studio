using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Who computed a stored verdict. Agent Studio's Review Plane names the card round it judged.</summary>
public sealed record ChangeReviewProvenance(string Producer, string? CardKey = null, int? Round = null,
    string? AttemptId = null, string? ClientId = null)
{
    public const string QualityStudio = "quality-studio";
    public const string AgentStudioReviewPlane = "agent-studio-review-plane";
    public static IReadOnlyList<string> Producers { get; } = [QualityStudio, AgentStudioReviewPlane];
}

/// <summary>One stored <c>change-review-verdict.v1</c> document, kept verbatim, with its key and provenance.</summary>
public sealed record ChangeReviewRecord(int SchemaVersion, ChangeReviewKey Key, string KeyDigest,
    ChangeReviewProvenance Provenance, DateTimeOffset RecordedAt, string DocumentDigest, JsonElement Document)
{
    public const int CurrentSchemaVersion = 1;

    [JsonIgnore]
    public string Verdict => Document.GetProperty("verdict").GetString() ?? string.Empty;

    /// <summary>A pass, concerns or block; an infrastructure verdict answers nothing about the code.</summary>
    [JsonIgnore]
    public bool IsDecisive => !ChangeReviewDocuments.IsInfrastructure(Verdict);
}

public enum ChangeReviewStoreOutcome
{
    /// <summary>The key had no record; this document is now its record.</summary>
    Stored,
    /// <summary>The key already held this exact document; nothing was written.</summary>
    Unchanged,
    /// <summary>The key held an infrastructure verdict, which this document replaced.</summary>
    Replaced,
    /// <summary>The key already holds a different decisive verdict, which is kept.</summary>
    Conflict,
}

public sealed record ChangeReviewPutResult(ChangeReviewStoreOutcome Outcome, ChangeReviewRecord Record);

/// <summary>
/// Change-review verdicts of one analysed working copy, filed in its data root under
/// <c>change-reviews/&lt;baseSha&gt;/&lt;headSha&gt;/&lt;key digest&gt;.json</c>.
/// <para>
/// A key (repository, range, policy hash, rule-set hash, catalogue version) holds one record. The
/// first decisive verdict for a key is final: a caller asking again gets the stored document and no
/// model call. An infrastructure verdict (<c>provider-unavailable</c>, <c>timeout</c>,
/// <c>auth-failure</c>, <c>unparseable</c>) is stored so it can be shown, but it never answers a
/// lookup and the next document for its key replaces it.
/// </para>
/// </summary>
public sealed class ChangeReviewStore
{
    public const string DirectoryName = "change-reviews";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, (SemaphoreSlim Gate, int Users)> gates = new(StringComparer.Ordinal);

    public ChangeReviewStore(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>The record of exactly this key, decisive or not.</summary>
    public ChangeReviewRecord? Find(string repositoryRoot, ChangeReviewKey key)
    {
        var path = RecordPath(repositoryRoot, key);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>Every record of one repository's range, the most recently recorded rule set first.</summary>
    public IReadOnlyList<ChangeReviewRecord> List(string repositoryRoot, string repository, string baseSha, string headSha)
    {
        RequireSha(baseSha);
        RequireSha(headSha);
        var directory = QualityDataRoot.Combine(repositoryRoot, DirectoryName, baseSha, headSha);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json")
            .Select(Read)
            .Where(record => string.Equals(record.Key.Repository, repository, StringComparison.Ordinal))
            .OrderByDescending(record => record.RecordedAt)
            .ThenBy(record => record.KeyDigest, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Stores one document under its key. Repeating the same document stores nothing new.</summary>
    public async Task<ChangeReviewPutResult> PutAsync(string repositoryRoot, ChangeReviewKey key,
        ChangeReviewProvenance provenance, JsonElement document, CancellationToken cancellationToken = default)
    {
        using var _ = await AcquireAsync(key.Digest, cancellationToken).ConfigureAwait(false);
        return await PutLockedAsync(repositoryRoot, key, provenance, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the decisive record of the key, or computes, stores and returns a new one. Concurrent
    /// callers for one key wait for the first, so a burst of identical requests costs one model call.
    /// </summary>
    public async Task<(ChangeReviewRecord Record, bool Computed)> GetOrComputeAsync(string repositoryRoot,
        ChangeReviewKey key, ChangeReviewProvenance provenance, Func<CancellationToken, Task<JsonElement>> compute,
        CancellationToken cancellationToken = default)
    {
        using var _ = await AcquireAsync(key.Digest, cancellationToken).ConfigureAwait(false);
        if (Find(repositoryRoot, key) is { IsDecisive: true } stored) return (stored, false);
        var document = await compute(cancellationToken).ConfigureAwait(false);
        var result = await PutLockedAsync(repositoryRoot, key, provenance, document, cancellationToken).ConfigureAwait(false);
        return (result.Record, true);
    }

    private async Task<ChangeReviewPutResult> PutLockedAsync(string repositoryRoot, ChangeReviewKey key,
        ChangeReviewProvenance provenance, JsonElement document, CancellationToken cancellationToken)
    {
        var digest = ChangeReviewDocuments.Digest(document);
        var existing = Find(repositoryRoot, key);
        if (existing is not null && existing.DocumentDigest == digest)
            return new(ChangeReviewStoreOutcome.Unchanged, existing);
        if (existing is { IsDecisive: true }) return new(ChangeReviewStoreOutcome.Conflict, existing);
        var record = new ChangeReviewRecord(ChangeReviewRecord.CurrentSchemaVersion, key, key.Digest, provenance,
            clock(), digest, document.Clone());
        await AtomicFile.WriteAllTextAsync(RecordPath(repositoryRoot, key),
            JsonSerializer.Serialize(record, Json), cancellationToken).ConfigureAwait(false);
        return new(existing is null ? ChangeReviewStoreOutcome.Stored : ChangeReviewStoreOutcome.Replaced, record);
    }

    private static string RecordPath(string repositoryRoot, ChangeReviewKey key)
    {
        RequireSha(key.BaseSha);
        RequireSha(key.HeadSha);
        return QualityDataRoot.Combine(repositoryRoot, DirectoryName, key.BaseSha, key.HeadSha,
            key.Digest["sha256:".Length..] + ".json");
    }

    private static ChangeReviewRecord Read(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ChangeReviewRecord>(File.ReadAllText(path), Json);
            if (record is not { SchemaVersion: ChangeReviewRecord.CurrentSchemaVersion } ||
                record.Key is null || record.Provenance is null ||
                record.Document.ValueKind != JsonValueKind.Object || record.KeyDigest != record.Key.Digest)
                throw new InvalidDataException($"Change-review record '{Path.GetFileName(path)}' is invalid.");
            return record;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Change-review record '{Path.GetFileName(path)}' is invalid.", exception);
        }
    }

    // A path segment, so only the exact SHA shape may reach the file system.
    private static void RequireSha(string value)
    {
        if (!ChangeReviewVerdictService.FullSha(value))
            throw new ArgumentException("Change-review ranges require full 40-character lowercase commit SHAs.");
    }

    private async Task<IDisposable> AcquireAsync(string digest, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (gates)
        {
            var (existing, users) = gates.TryGetValue(digest, out var found) ? found : (new SemaphoreSlim(1, 1), 0);
            gates[digest] = (existing, users + 1);
            gate = existing;
        }
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(digest, entered: false);
            throw;
        }
        return new Lease(() => Release(digest, entered: true));
    }

    private void Release(string digest, bool entered)
    {
        lock (gates)
        {
            var (gate, users) = gates[digest];
            if (entered) gate.Release();
            if (users == 1)
            {
                gates.Remove(digest);
                gate.Dispose();
            }
            else gates[digest] = (gate, users - 1);
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

/// <summary>Checks an ingested <c>change-review-verdict.v1</c> document against its schema.</summary>
public static class ChangeReviewDocuments
{
    public static IReadOnlyList<string> InfrastructureVerdicts { get; } =
        ["provider-unavailable", "timeout", "auth-failure", "unparseable"];

    private static readonly string[] Verdicts = ["pass", "concerns", "block", .. InfrastructureVerdicts];
    private static readonly string[] Severities = ["critical", "high", "medium", "low", "info"];
    private static readonly HashSet<string> DocumentProperties = new(StringComparer.Ordinal)
    {
        "schema", "schemaVersion", "repository", "baseSha", "headSha", "verdict", "policyHash", "ruleSetHash",
        "catalogueVersion", "findings", "failureReason", "model", "thinkingLevel", "usage", "durationMilliseconds",
    };
    private static readonly HashSet<string> FindingProperties = new(StringComparer.Ordinal)
    {
        "id", "fingerprint", "ruleId", "ruleVersion", "severity", "path", "side", "line", "message",
        "evidenceChecked", "missing", "disposition",
    };

    public static bool IsInfrastructure(string? verdict) =>
        InfrastructureVerdicts.Any(value => string.Equals(value, verdict, StringComparison.Ordinal));

    /// <summary>True only for the schema id and version this host can read; anything else is a newer contract.</summary>
    public static bool IsSupportedVersion(JsonElement document) =>
        document.ValueKind == JsonValueKind.Object &&
        document.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var number) && number == 1 &&
        document.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.String &&
        schema.GetString() == ChangeReviewVerdictService.SchemaId;

    /// <summary>Every way a supported-version document departs from the v1 schema; empty when it conforms.</summary>
    public static IReadOnlyList<string> Validate(JsonElement document)
    {
        var errors = new List<string>();
        if (document.ValueKind != JsonValueKind.Object) return ["The document must be a JSON object."];
        foreach (var property in document.EnumerateObject())
            if (!DocumentProperties.Contains(property.Name)) errors.Add($"Unknown property '{property.Name}'.");
        RequireString(document, "repository", errors);
        RequirePattern(document, "baseSha", ChangeReviewVerdictService.FullSha, "a 40-character lowercase SHA", errors);
        RequirePattern(document, "headSha", ChangeReviewVerdictService.FullSha, "a 40-character lowercase SHA", errors);
        RequirePattern(document, "verdict", value => Verdicts.Any(verdict => verdict == value), "one of " + string.Join(", ", Verdicts), errors);
        RequirePattern(document, "policyHash", IsHash, "a sha256: hash", errors);
        RequirePattern(document, "ruleSetHash", IsHash, "a sha256: hash", errors);
        RequireString(document, "catalogueVersion", errors);
        foreach (var name in new[] { "failureReason", "model", "thinkingLevel" })
            if (document.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                errors.Add($"'{name}' must be a string or null.");
        if (document.TryGetProperty("usage", out var usage) && usage.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            errors.Add("'usage' must be an object or null.");
        if (document.TryGetProperty("durationMilliseconds", out var duration) && duration.ValueKind != JsonValueKind.Null &&
            !(duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out var milliseconds) && milliseconds >= 0))
            errors.Add("'durationMilliseconds' must be a non-negative integer or null.");
        if (!document.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            errors.Add("'findings' must be an array.");
            return errors;
        }
        var index = 0;
        foreach (var finding in findings.EnumerateArray())
        {
            var prefix = $"findings[{index++}]";
            if (finding.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{prefix} must be an object.");
                continue;
            }
            foreach (var property in finding.EnumerateObject())
                if (!FindingProperties.Contains(property.Name)) errors.Add($"{prefix}: unknown property '{property.Name}'.");
            RequirePattern(finding, "id", value => value.StartsWith("finding-", StringComparison.Ordinal) &&
                IsHex(value["finding-".Length..]), "finding-<64 hex>", errors, prefix);
            RequirePattern(finding, "fingerprint", IsHash, "a sha256: hash", errors, prefix);
            foreach (var name in new[] { "ruleId", "ruleVersion", "path", "message", "evidenceChecked", "missing" })
                RequireString(finding, name, errors, prefix);
            RequirePattern(finding, "severity", value => Severities.Any(severity => severity == value), "a severity", errors, prefix);
            RequirePattern(finding, "side", value => value is "base" or "head", "base or head", errors, prefix);
            RequirePattern(finding, "disposition", value => value is "concerns" or "block", "concerns or block", errors, prefix);
            if (!finding.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number ||
                !line.TryGetInt32(out var number) || number < 1)
                errors.Add($"{prefix}: 'line' must be a positive integer.");
        }
        return errors;
    }

    /// <summary>The key a conforming document was computed for.</summary>
    public static ChangeReviewKey KeyOf(JsonElement document) => new(
        document.GetProperty("repository").GetString()!, document.GetProperty("baseSha").GetString()!,
        document.GetProperty("headSha").GetString()!, document.GetProperty("policyHash").GetString()!,
        document.GetProperty("ruleSetHash").GetString()!, document.GetProperty("catalogueVersion").GetString()!);

    /// <summary>A digest of the document's compact JSON, so formatting alone never makes a second record.</summary>
    public static string Digest(JsonElement document) =>
        ChangeReviewPolicy.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document)));

    private static bool IsHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value["sha256:".Length..]);

    private static bool IsHex(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireString(JsonElement element, string name, List<string> errors, string? prefix = null) =>
        RequirePattern(element, name, _ => true, "a non-empty string", errors, prefix);

    private static void RequirePattern(JsonElement element, string name, Func<string, bool> accepts,
        string expectation, List<string> errors, string? prefix = null)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) || !accepts(value.GetString()!))
            errors.Add($"{(prefix is null ? string.Empty : prefix + ": ")}'{name}' must be {expectation}.");
    }
}
