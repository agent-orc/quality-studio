using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingAssessmentEvent(
    int SchemaVersion,
    string EventId,
    string Fingerprint,
    string Status,
    string Actor,
    string Reason,
    DateTimeOffset AssessedAt,
    string? ReviewRunId = null,
    string? OperationRunId = null,
    bool CompatibilityProjection = false);

public sealed record FindingResolutionEvent(
    int SchemaVersion,
    string EventId,
    string Fingerprint,
    string Status,
    string Actor,
    string Reason,
    DateTimeOffset ResolvedAt,
    string? TaskKey = null,
    bool CompatibilityProjection = false);

public sealed record FindingDecisionSnapshot(
    IReadOnlyDictionary<string, FindingAssessmentEvent> Assessments,
    IReadOnlyDictionary<string, FindingResolutionEvent> Resolutions);

public sealed class FindingDecisionConflictException(string fingerprint)
    : Exception($"Finding decision '{fingerprint}' changed after it was loaded.");

/// <summary>Append-only human truth and remediation events, independent of finding observations.</summary>
public sealed class FindingDecisionStore
{
    public const string AssessmentRelativePath = ".quality/findings/assessments";
    public const string ResolutionRelativePath = ".quality/findings/resolutions";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root;
    private readonly Func<DateTimeOffset> clock;

    public FindingDecisionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        root = Path.GetFullPath(repositoryRoot);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FindingDecisionSnapshot> ReadAsync(
        IReadOnlyDictionary<string, FindingStateRecord>? legacyStates = null,
        CancellationToken cancellationToken = default)
    {
        var assessments = (await ReadEventsAsync<FindingAssessmentEvent>(AssessmentRelativePath, cancellationToken)
            .ConfigureAwait(false)).GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.AssessedAt).ThenBy(item => item.EventId, StringComparer.Ordinal).Last(), StringComparer.Ordinal);
        var resolutions = (await ReadEventsAsync<FindingResolutionEvent>(ResolutionRelativePath, cancellationToken)
            .ConfigureAwait(false)).GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.ResolvedAt).ThenBy(item => item.EventId, StringComparer.Ordinal).Last(), StringComparer.Ordinal);

        foreach (var (fingerprint, legacy) in legacyStates ?? new Dictionary<string, FindingStateRecord>())
        {
            if (!assessments.ContainsKey(fingerprint)) assessments[fingerprint] = CompatibilityAssessment(legacy);
            if (!resolutions.ContainsKey(fingerprint)) resolutions[fingerprint] = CompatibilityResolution(legacy);
        }
        return new FindingDecisionSnapshot(assessments, resolutions);
    }

    public Task<FindingAssessmentEvent> AppendAssessmentAsync(
        string fingerprint,
        string status,
        string actor,
        string reason,
        DateTimeOffset? expectedAssessedAt = null,
        string? reviewRunId = null,
        string? operationRunId = null,
        CancellationToken cancellationToken = default) =>
        AppendLockedAsync(async () =>
        {
            ValidateFingerprint(fingerprint);
            if (status is not ("unassessed" or "confirmed" or "dismissed" or "disputed"))
                throw new ArgumentException("Assessment must be unassessed, confirmed, dismissed, or disputed.", nameof(status));
            ValidateActorReason(actor, reason);
            var legacy = await new FindingStateStore(root).ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = (await ReadAsync(legacy, cancellationToken).ConfigureAwait(false))
                .Assessments.GetValueOrDefault(fingerprint);
            if (current?.AssessedAt != expectedAssessedAt)
                throw new FindingDecisionConflictException(fingerprint);
            var now = NextTimestamp(clock().ToUniversalTime(), current?.AssessedAt);
            var item = new FindingAssessmentEvent(1, "assessment-" + Guid.NewGuid().ToString("N"), fingerprint,
                status, actor.Trim(), reason.Trim(), now, reviewRunId, operationRunId);
            await AppendLineAsync(AssessmentRelativePath, now, item, cancellationToken).ConfigureAwait(false);
            return item;
        }, cancellationToken);

    public Task<FindingResolutionEvent> AppendResolutionAsync(
        string fingerprint,
        string status,
        string actor,
        string reason,
        DateTimeOffset? expectedResolvedAt = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default) =>
        AppendLockedAsync(async () =>
        {
            ValidateFingerprint(fingerprint);
            if (status is not ("open" or "planned" or "fixed" or "risk-accepted" or "obsolete"))
                throw new ArgumentException("Resolution must be open, planned, fixed, risk-accepted, or obsolete.", nameof(status));
            ValidateActorReason(actor, reason);
            var legacy = await new FindingStateStore(root).ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = (await ReadAsync(legacy, cancellationToken).ConfigureAwait(false))
                .Resolutions.GetValueOrDefault(fingerprint);
            if (current?.ResolvedAt != expectedResolvedAt)
                throw new FindingDecisionConflictException(fingerprint);
            var now = NextTimestamp(clock().ToUniversalTime(), current?.ResolvedAt);
            var item = new FindingResolutionEvent(1, "resolution-" + Guid.NewGuid().ToString("N"), fingerprint,
                status, actor.Trim(), reason.Trim(), now, Text(taskKey));
            await AppendLineAsync(ResolutionRelativePath, now, item, cancellationToken).ConfigureAwait(false);
            return item;
        }, cancellationToken);

    private async Task<T> AppendLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task AppendLineAsync<T>(string relativeDirectory, DateTimeOffset timestamp, T item,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, timestamp.ToString("yyyy-MM") + ".jsonl");
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, JsonOptions) + "\n");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> ReadEventsAsync<T>(string relativeDirectory, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl").Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var item = JsonSerializer.Deserialize<T>(line, JsonOptions)
                    ?? throw new JsonException($"Finding decision line in '{Path.GetFileName(path)}' is empty.");
                result.Add(item);
            }
        }
        return result;
    }

    private static FindingAssessmentEvent CompatibilityAssessment(FindingStateRecord state) => new(
        1, "compatibility-" + state.Fingerprint[7..19], state.Fingerprint,
        state.State switch
        {
            FindingState.Accepted or FindingState.Waived => "confirmed",
            FindingState.FalsePositive => "dismissed",
            _ => "unassessed",
        }, state.Author, $"Compatibility projection from legacy {FindingStateStore.StateName(state.State)}: {state.Reason}",
        state.Timestamp, CompatibilityProjection: true);

    private static FindingResolutionEvent CompatibilityResolution(FindingStateRecord state) => new(
        1, "compatibility-" + state.Fingerprint[7..19], state.Fingerprint,
        state.State switch
        {
            FindingState.Waived => "risk-accepted",
            FindingState.FalsePositive => "obsolete",
            FindingState.Resolved => "fixed",
            _ => "open",
        }, state.Author, $"Compatibility projection from legacy {FindingStateStore.StateName(state.State)}: {state.Reason}",
        state.Timestamp, CompatibilityProjection: true);

    private static void ValidateActorReason(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200)
            throw new ArgumentException("Decision actor must contain 1 to 200 characters.", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new ArgumentException("Decision reason must contain 1 to 2,000 characters.", nameof(reason));
    }

    internal static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint.Length != 71 || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
            !fingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Finding fingerprint must be a lowercase SHA-256 value.", nameof(fingerprint));
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset NextTimestamp(DateTimeOffset candidate, DateTimeOffset? current) =>
        current is not null && candidate <= current ? current.Value.AddTicks(1) : candidate;
}

public sealed record FindingSuppressionMatch(
    string? Fingerprint = null,
    string? RuleId = null,
    string? PathPattern = null,
    IReadOnlyList<string>? ReviewKinds = null,
    IReadOnlyList<string>? SourceKinds = null);

public sealed record FindingSuppressionRule(
    string Id,
    bool Enabled,
    FindingSuppressionMatch Match,
    string Effect,
    string Reason,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record FindingSuppressionDocument(
    int SchemaVersion,
    long Revision,
    IReadOnlyList<FindingSuppressionRule> Rules);

public sealed record FindingSuppressionCandidate(
    string Fingerprint,
    string RuleId,
    string Path,
    string ReviewKind,
    string SourceKind,
    string Title);

public sealed record FindingSuppressionPreview(
    FindingSuppressionRule Rule,
    IReadOnlyList<FindingSuppressionCandidate> Matches,
    bool Broad);

/// <summary>Revisioned repository-owned ignore policy. Observations are never removed.</summary>
public sealed class FindingSuppressionStore
{
    public const string RelativePath = ".quality/findings/suppressions.json";
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly object gate;
    private readonly Func<DateTimeOffset> clock;

    public FindingSuppressionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        path = Path.Combine(root, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        gate = Gates.GetOrAdd(path, _ => new object());
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FindingSuppressionDocument Read()
    {
        lock (gate) return ReadCore();
    }

    public FindingSuppressionPreview Preview(FindingSuppressionRule rule,
        IEnumerable<FindingSuppressionCandidate> candidates)
    {
        var normalized = Validate(rule);
        var matches = candidates.Where(candidate => Matches(normalized, candidate, clock().ToUniversalTime()))
            .OrderBy(candidate => candidate.Path, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Fingerprint, StringComparer.Ordinal).ToArray();
        return new FindingSuppressionPreview(normalized, matches, normalized.Match.Fingerprint is null);
    }

    public FindingSuppressionDocument Add(FindingSuppressionRule rule, long expectedRevision,
        IEnumerable<FindingSuppressionCandidate> candidates, bool confirmBroad)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new FindingSuppressionConflictException(current.Revision);
            var preview = Preview(rule, candidates);
            if (preview.Broad && !confirmBroad)
                throw new ArgumentException("A broad suppression requires preview and explicit confirmation.");
            if (current.Rules.Any(existing => existing.Id == preview.Rule.Id))
                throw new ArgumentException($"Suppression rule '{preview.Rule.Id}' already exists.");
            return Write(current with { Revision = current.Revision + 1, Rules = current.Rules.Append(preview.Rule).ToArray() });
        }
    }

    public FindingSuppressionDocument Remove(string id, long expectedRevision)
    {
        lock (gate)
        {
            var current = ReadCore();
            if (current.Revision != expectedRevision)
                throw new FindingSuppressionConflictException(current.Revision);
            if (!current.Rules.Any(rule => rule.Id == id)) throw new KeyNotFoundException($"Suppression rule '{id}' was not found.");
            return Write(current with { Revision = current.Revision + 1, Rules = current.Rules.Where(rule => rule.Id != id).ToArray() });
        }
    }

    public static bool Matches(FindingSuppressionRule rule, FindingSuppressionCandidate candidate, DateTimeOffset now)
    {
        if (!rule.Enabled || rule.Effect != "suppress" || rule.ExpiresAt is not null && rule.ExpiresAt <= now) return false;
        var match = rule.Match;
        return (match.Fingerprint is null || match.Fingerprint == candidate.Fingerprint) &&
               (match.RuleId is null || match.RuleId == candidate.RuleId) &&
               (match.PathPattern is null || RepositoryScope.PatternMatches(match.PathPattern, candidate.Path)) &&
               (match.ReviewKinds is not { Count: > 0 } || match.ReviewKinds.Contains(candidate.ReviewKind, StringComparer.Ordinal)) &&
               (match.SourceKinds is not { Count: > 0 } || match.SourceKinds.Contains(candidate.SourceKind, StringComparer.Ordinal));
    }

    private FindingSuppressionDocument ReadCore()
    {
        if (!File.Exists(path)) return new FindingSuppressionDocument(1, 0, []);
        var document = JsonSerializer.Deserialize<FindingSuppressionDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Finding suppressions must be a JSON object.");
        if (document.SchemaVersion != 1 || document.Revision < 0 || document.Rules is null)
            throw new JsonException("Finding suppressions use an unsupported schema or invalid revision.");
        if (document.Rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding suppressions contain duplicate rule ids.");
        return document with { Rules = document.Rules.Select(Validate).ToArray() };
    }

    private FindingSuppressionDocument Write(FindingSuppressionDocument document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return document;
    }

    private FindingSuppressionRule Validate(FindingSuppressionRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 200 ||
            rule.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Suppression id must contain 1 to 200 safe identifier characters.");
        if (rule.Effect != "suppress") throw new ArgumentException("Suppression effect must be suppress.");
        if (string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length > 2000 ||
            string.IsNullOrWhiteSpace(rule.Author) || rule.Author.Length > 200)
            throw new ArgumentException("Suppression author and reason are required and bounded.");
        if (rule.ExpiresAt is not null && rule.ExpiresAt <= rule.CreatedAt)
            throw new ArgumentException("Suppression expiry must be after its creation time.");
        var match = rule.Match;
        if (match.Fingerprint is not null) FindingDecisionStore.ValidateFingerprint(match.Fingerprint);
        if (match.Fingerprint is null && string.IsNullOrWhiteSpace(match.RuleId) && string.IsNullOrWhiteSpace(match.PathPattern) &&
            match.ReviewKinds is not { Count: > 0 } && match.SourceKinds is not { Count: > 0 })
            throw new ArgumentException("Suppression requires at least one stable match field.");
        if (match.PathPattern?.Contains("..", StringComparison.Ordinal) == true)
            throw new ArgumentException("Suppression path patterns cannot traverse parents.");
        if ((match.ReviewKinds ?? []).Any(kind => kind is not ("code" or "security" or "performance")) ||
            (match.SourceKinds ?? []).Any(kind => kind is not ("agent" or "deterministic")))
            throw new ArgumentException("Suppression review/source kinds are invalid.");
        return rule with
        {
            Id = rule.Id.Trim(), Reason = rule.Reason.Trim(), Author = rule.Author.Trim(),
            Match = match with
            {
                RuleId = Text(match.RuleId), PathPattern = Text(match.PathPattern)?.Replace('\\', '/').TrimStart('/'),
                ReviewKinds = (match.ReviewKinds ?? []).Distinct(StringComparer.Ordinal).ToArray(),
                SourceKinds = (match.SourceKinds ?? []).Distinct(StringComparer.Ordinal).ToArray(),
            },
        };
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class FindingSuppressionConflictException(long currentRevision)
    : Exception("Finding suppression policy changed after it was loaded.")
{
    public long CurrentRevision { get; } = currentRevision;
}

public static class FindingDecisionProjection
{
    public static JsonObject Apply(JsonObject projectedMetadata, FindingDecisionSnapshot decisions,
        FindingSuppressionDocument suppressions, DateTimeOffset now)
    {
        var result = projectedMetadata.DeepClone().AsObject();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["unassessed"] = 0, ["confirmed"] = 0, ["dismissed"] = 0, ["disputed"] = 0,
            ["open"] = 0, ["planned"] = 0, ["fixed"] = 0, ["risk-accepted"] = 0, ["obsolete"] = 0,
            ["suppressed"] = 0,
        };
        var includedWeight = 0;
        var suppressedWeight = 0;
        var reviewKind = result["kind"]?.GetValue<string>() ?? "code";
        foreach (var finding in result["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var fingerprint = finding["fingerprint"]?.GetValue<string>() ?? string.Empty;
            var assessment = decisions.Assessments.GetValueOrDefault(fingerprint);
            var resolution = decisions.Resolutions.GetValueOrDefault(fingerprint);
            if (assessment is not null)
                finding["assessment"] = JsonSerializer.SerializeToNode(assessment, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (resolution is not null)
                finding["resolution"] = JsonSerializer.SerializeToNode(resolution, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            counts[assessment?.Status ?? "unassessed"]++;
            counts[resolution?.Status ?? "open"]++;

            var location = finding["locations"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
            var candidate = new FindingSuppressionCandidate(
                fingerprint,
                finding["ruleId"]?.GetValue<string>() ?? string.Empty,
                location?["path"]?.GetValue<string>() ?? string.Empty,
                reviewKind,
                finding["source"] is JsonObject ? "deterministic" : "agent",
                finding["title"]?.GetValue<string>() ?? string.Empty);
            var suppression = suppressions.Rules.LastOrDefault(rule => FindingSuppressionStore.Matches(rule, candidate, now));
            finding["suppressed"] = suppression is not null;
            if (suppression is not null)
            {
                finding["suppression"] = JsonSerializer.SerializeToNode(suppression, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                counts["suppressed"]++;
            }

            if (finding["state"]?.GetValue<string>() is "waived" or "false-positive" or "resolved") continue;
            var weight = SeverityWeight(finding["severity"]?.GetValue<string>());
            if (suppression is null) includedWeight += weight; else suppressedWeight += weight;
        }
        result["decisionCounts"] = new JsonObject(counts.Select(pair =>
            KeyValuePair.Create<string, JsonNode?>(pair.Key == "risk-accepted" ? "riskAccepted" : pair.Key, pair.Value)));
        ApplySuppressedGrade(result, includedWeight, suppressedWeight);
        return result;
    }

    private static void ApplySuppressedGrade(JsonObject metadata, int includedWeight, int suppressedWeight)
    {
        if (suppressedWeight == 0 || metadata["grade"] is not JsonObject grade ||
            grade["score"] is not JsonValue scoreNode || !scoreNode.TryGetValue<int>(out var score)) return;
        var total = includedWeight + suppressedWeight;
        var adjusted = total == 0 ? 100 : (int)Math.Round(
            100 - (100 - score) * (includedWeight / (double)total), MidpointRounding.AwayFromZero);
        grade["score"] = Math.Clamp(adjusted, score, 100);
        grade["band"] = adjusted switch { >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 60 => "D", _ => "F" };
        grade["rationale"] = grade["rationale"]!.GetValue<string>() +
            " Suppressed findings are excluded from this effective grade but remain observable.";
    }

    private static int SeverityWeight(string? severity) => severity switch
    {
        "critical" => 16, "high" => 8, "medium" => 4, "low" => 2, _ => 1,
    };
}
