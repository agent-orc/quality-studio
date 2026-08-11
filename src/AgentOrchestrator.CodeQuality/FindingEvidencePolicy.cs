using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public enum FindingAssessmentStatus { Unassessed, Confirmed, Dismissed, Disputed }
public enum FindingResolutionStatus { Open, Planned, Fixed, RiskAccepted, Obsolete, FixedByAbsence }

public sealed record FindingAssessmentEvent(
    int SchemaVersion,
    long Revision,
    string EventId,
    string Fingerprint,
    string FindingId,
    string Path,
    string RuleId,
    FindingAssessmentStatus Assessment,
    FindingResolutionStatus Resolution,
    string Actor,
    string Reason,
    DateTimeOffset OccurredAt,
    string Source = "human",
    string? ReviewRunId = null,
    string? OperationRunId = null,
    string? TaskKey = null);

public sealed record FindingAssessmentProjection(long Revision, IReadOnlyDictionary<string, FindingAssessmentEvent> Findings);

public sealed class FindingAssessmentStore
{
    public const string RelativePath = ".quality/findings/assessments";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly string repositoryRoot;
    private readonly string directory;
    private readonly Func<DateTimeOffset> clock;

    public FindingAssessmentStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        directory = Path.Combine(this.repositoryRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<FindingAssessmentProjection> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAtAsync(null, cancellationToken);

    /// <summary>
    /// Reconstructs the latest assessment state no later than a durable evidence cutoff.
    /// A null cutoff returns the current projection.
    /// </summary>
    public async Task<FindingAssessmentProjection> ReadAtAsync(
        DateTimeOffset? cutoff,
        CancellationToken cancellationToken = default)
    {
        var evidenceCutoff = cutoff?.ToUniversalTime();
        var events = (await ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => evidenceCutoff is null || item.OccurredAt <= evidenceCutoff)
            .ToArray();
        var latest = events.GroupBy(item => item.Fingerprint, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Revision).Last(), StringComparer.Ordinal);
        var states = await new FindingStateStore(repositoryRoot, clock).ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var state in states.Values.Where(state =>
                     !latest.ContainsKey(state.Fingerprint) &&
                     (evidenceCutoff is null || state.Timestamp <= evidenceCutoff)))
            latest[state.Fingerprint] = Compatibility(state);
        return new(events.Select(item => item.Revision).DefaultIfEmpty().Max(), latest);
    }

    public async Task<FindingAssessmentEvent> AppendAsync(
        FindingIdentityRecord finding,
        FindingAssessmentStatus? assessment,
        FindingResolutionStatus? resolution,
        string actor,
        string reason,
        long expectedRevision,
        string? reviewRunId = null,
        string? operationRunId = null,
        string? taskKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ValidateText(actor, reason);
        if (assessment is null && resolution is null) throw new ArgumentException("Assessment or resolution is required.");
        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var projection = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var current = projection.Findings.GetValueOrDefault(finding.Fingerprint);
            var currentRevision = current?.Source == "compatibility" ? 0 : current?.Revision ?? 0;
            if (expectedRevision != currentRevision)
                throw new FindingAssessmentConflictException(finding.Fingerprint, currentRevision);
            var now = clock().ToUniversalTime();
            var entry = new FindingAssessmentEvent(
                1,
                projection.Revision + 1,
                "assessment-" + Guid.NewGuid().ToString("N"),
                finding.Fingerprint,
                finding.Id,
                finding.Path,
                finding.RuleId,
                assessment ?? current?.Assessment ?? FindingAssessmentStatus.Unassessed,
                resolution ?? current?.Resolution ?? FindingResolutionStatus.Open,
                actor.Trim(),
                reason.Trim(),
                now,
                "human",
                reviewRunId,
                operationRunId,
                string.IsNullOrWhiteSpace(taskKey) ? null : taskKey.Trim());
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, now.ToString("yyyy-MM") + ".jsonl");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<FindingAssessmentEvent>> ReadEventsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<FindingAssessmentEvent>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl").Order(StringComparer.Ordinal))
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<FindingAssessmentEvent>(line, JsonOptions);
                    if (item is not null && item.SchemaVersion == 1 && item.Revision > 0 && IsFingerprint(item.Fingerprint)) result.Add(item);
                }
                catch (JsonException) when (index == lines.Length - 1)
                {
                    // A crash may truncate only the final append.
                }
            }
        }
        return result.OrderBy(item => item.Revision).ToArray();
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".append.lock");
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FindingAssessmentEvent Compatibility(FindingStateRecord state)
    {
        var (assessment, resolution) = state.State switch
        {
            FindingState.Accepted => (FindingAssessmentStatus.Confirmed, FindingResolutionStatus.Open),
            FindingState.Waived => (FindingAssessmentStatus.Confirmed, FindingResolutionStatus.RiskAccepted),
            FindingState.FalsePositive => (FindingAssessmentStatus.Dismissed, FindingResolutionStatus.Obsolete),
            FindingState.Resolved => (FindingAssessmentStatus.Unassessed, FindingResolutionStatus.FixedByAbsence),
            _ => (FindingAssessmentStatus.Unassessed, FindingResolutionStatus.Open),
        };
        return new(1, 0, "compatibility:" + state.Fingerprint, state.Fingerprint, state.FindingId, state.Path,
            state.RuleId, assessment, resolution, state.Author, state.Reason, state.Timestamp, "compatibility",
            TaskKey: null);
    }

    private static void ValidateText(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200) throw new ArgumentException("Assessment actor is required and cannot exceed 200 characters.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2_000) throw new ArgumentException("Assessment reason is required and cannot exceed 2,000 characters.");
    }

    private static bool IsFingerprint(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }
}

public sealed class FindingAssessmentConflictException(string fingerprint, long currentRevision)
    : Exception($"Finding '{fingerprint}' assessment changed after it was loaded.")
{
    public long CurrentRevision { get; } = currentRevision;
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

public sealed record FindingSuppressionDocument(int SchemaVersion, long Revision, IReadOnlyList<FindingSuppressionRule> Rules);
public sealed record FindingObservation(string Fingerprint, string RuleId, string Path, string ReviewKind, string SourceKind, string FindingId, string Title);

public sealed class FindingSuppressionStore
{
    public const string RelativePath = ".quality/findings/suppressions.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly Func<DateTimeOffset> clock;

    public FindingSuppressionStore(string repositoryRoot, Func<DateTimeOffset>? clock = null)
    {
        path = Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<FindingSuppressionDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new(1, 0, []);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var document = await JsonSerializer.DeserializeAsync<FindingSuppressionDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("Finding suppressions must be a JSON object.");
        Validate(document);
        return document;
    }

    public async Task<FindingSuppressionDocument> SetAsync(
        FindingSuppressionRule rule,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateRule(rule, clock().ToUniversalTime());
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var document = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (document.Revision != expectedRevision) throw new FindingSuppressionConflictException(document.Revision);
            var rules = document.Rules.Where(candidate => !string.Equals(candidate.Id, rule.Id, StringComparison.Ordinal)).Append(rule)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
            var updated = new FindingSuppressionDocument(1, document.Revision + 1, rules);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, JsonOptions) + Environment.NewLine,
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lockPath = path + ".lock";
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<FindingObservation> Preview(FindingSuppressionRule rule, IEnumerable<FindingObservation> observations) =>
        observations.Where(observation => Matches(rule, observation, clock().ToUniversalTime()))
            .OrderBy(observation => observation.Path, StringComparer.Ordinal).ThenBy(observation => observation.FindingId, StringComparer.Ordinal).ToArray();

    public static FindingSuppressionRule? Match(
        FindingSuppressionDocument document,
        FindingObservation observation,
        DateTimeOffset now) => document.Rules.FirstOrDefault(rule => Matches(rule, observation, now));

    private static bool Matches(FindingSuppressionRule rule, FindingObservation observation, DateTimeOffset now)
    {
        if (!rule.Enabled || rule.Effect != "suppress" || rule.ExpiresAt is not null && rule.ExpiresAt <= now) return false;
        var match = rule.Match;
        return (match.Fingerprint is null || string.Equals(match.Fingerprint, observation.Fingerprint, StringComparison.Ordinal)) &&
               (match.RuleId is null || string.Equals(match.RuleId, observation.RuleId, StringComparison.Ordinal)) &&
               (match.PathPattern is null || GlobMatch(match.PathPattern, observation.Path)) &&
               (match.ReviewKinds is null || match.ReviewKinds.Contains(observation.ReviewKind, StringComparer.Ordinal)) &&
               (match.SourceKinds is null || match.SourceKinds.Contains(observation.SourceKind, StringComparer.Ordinal));
    }

    private static bool GlobMatch(string pattern, string path)
    {
        var patternParts = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathParts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchParts(0, 0);

        bool MatchParts(int patternIndex, int pathIndex)
        {
            if (patternIndex == patternParts.Length) return pathIndex == pathParts.Length;
            if (patternParts[patternIndex] == "**")
                return MatchParts(patternIndex + 1, pathIndex) || pathIndex < pathParts.Length && MatchParts(patternIndex, pathIndex + 1);
            return pathIndex < pathParts.Length && MatchSegment(patternParts[patternIndex], pathParts[pathIndex]) &&
                   MatchParts(patternIndex + 1, pathIndex + 1);
        }
    }

    private static bool MatchSegment(string pattern, string value)
    {
        var matches = new bool[value.Length + 1];
        matches[0] = true;
        foreach (var token in pattern)
        {
            var next = new bool[value.Length + 1];
            if (token == '*')
            {
                next[0] = matches[0];
                for (var index = 1; index <= value.Length; index++) next[index] = matches[index] || next[index - 1];
            }
            else
            {
                for (var index = 1; index <= value.Length; index++)
                    next[index] = matches[index - 1] && (token == '?' || token == value[index - 1]);
            }
            matches = next;
        }
        return matches[value.Length];
    }

    private static void Validate(FindingSuppressionDocument document)
    {
        if (document.SchemaVersion != 1 || document.Revision < 0 || document.Rules is null ||
            document.Rules.GroupBy(rule => rule.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new JsonException("Finding suppression document is invalid.");
        foreach (var rule in document.Rules) ValidateRule(rule, DateTimeOffset.MinValue);
    }

    private static void ValidateRule(FindingSuppressionRule rule, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 ||
            rule.Id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Suppression rule id is invalid.");
        if (rule.Effect != "suppress") throw new ArgumentException("Suppression effect must be 'suppress'.");
        if (string.IsNullOrWhiteSpace(rule.Reason) || rule.Reason.Length > 2_000 ||
            string.IsNullOrWhiteSpace(rule.Author) || rule.Author.Length > 200)
            throw new ArgumentException("Suppression reason and author are required and must fit their length limits.");
        if (rule.CreatedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Suppression creation time must be UTC.");
        if (rule.ExpiresAt is not null && rule.ExpiresAt <= now) throw new ArgumentException("Suppression expiry must be in the future.");
        var match = rule.Match ?? throw new ArgumentException("Suppression match is required.");
        if (match.Fingerprint is null && match.RuleId is null && match.PathPattern is null &&
            match.ReviewKinds is null && match.SourceKinds is null) throw new ArgumentException("Suppression rule must have a stable match field.");
        if (match.Fingerprint is not null && !IsFingerprint(match.Fingerprint))
            throw new ArgumentException("Suppression fingerprint is invalid.");
        if (match.RuleId is { Length: > 200 }) throw new ArgumentException("Suppression rule id match is too long.");
        if (match.Fingerprint is not null && (match.RuleId is not null || match.PathPattern is not null || match.ReviewKinds is not null || match.SourceKinds is not null))
            throw new ArgumentException("An exact fingerprint rule cannot be combined with a broad scope.");
        if (match.PathPattern?.Contains('\\') == true ||
            match.PathPattern?.Split('/').Any(segment => segment is "." or ".." or "") == true)
            throw new ArgumentException("Suppression path pattern must be a normalized repository glob.");
        if (match.ReviewKinds?.Any(kind => kind is not ("code" or "security" or "performance")) == true)
            throw new ArgumentException("Suppression review kind is invalid.");
        if (match.SourceKinds?.Any(kind => kind is not ("agent" or "deterministic")) == true)
            throw new ArgumentException("Suppression source kind is invalid.");
    }

    private static bool IsFingerprint(string value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class FindingSuppressionConflictException(long currentRevision)
    : Exception("Finding suppressions changed after they were loaded.")
{
    public long CurrentRevision { get; } = currentRevision;
}

public sealed record FindingPolicySnapshot(
    FindingAssessmentProjection Assessments,
    FindingSuppressionDocument Suppressions,
    DateTimeOffset At);

public static class FindingEvidencePolicyProjection
{
    public static JsonObject Apply(JsonObject metadata, FindingPolicySnapshot snapshot)
    {
        var result = metadata.DeepClone().AsObject();
        var assessmentCounts = Enum.GetNames<FindingAssessmentStatus>().ToDictionary(ToKebab, _ => 0, StringComparer.Ordinal);
        var resolutionCounts = Enum.GetNames<FindingResolutionStatus>().ToDictionary(ToKebab, _ => 0, StringComparer.Ordinal);
        var suppressed = 0;
        foreach (var finding in result["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var fingerprint = finding["fingerprint"]?.GetValue<string>() ?? string.Empty;
            var projection = snapshot.Assessments.Findings.GetValueOrDefault(fingerprint);
            var assessment = projection?.Assessment ?? FindingAssessmentStatus.Unassessed;
            var resolution = projection?.Resolution ?? FindingResolutionStatus.Open;
            assessmentCounts[ToKebab(assessment.ToString())]++;
            resolutionCounts[ToKebab(resolution.ToString())]++;
            finding["assessment"] = new JsonObject
            {
                ["status"] = ToKebab(assessment.ToString()),
                ["assessedBy"] = projection?.Actor,
                ["reason"] = projection?.Reason,
                ["assessedAt"] = projection?.OccurredAt.ToUniversalTime().ToString("O"),
                ["revision"] = projection?.Source == "compatibility" ? 0 : projection?.Revision ?? 0,
                ["source"] = projection?.Source ?? "none",
            };
            finding["resolution"] = new JsonObject
            {
                ["status"] = ToKebab(resolution.ToString()),
                ["taskKey"] = projection?.TaskKey,
                ["resolvedAt"] = resolution is FindingResolutionStatus.Fixed or FindingResolutionStatus.Obsolete or FindingResolutionStatus.FixedByAbsence
                    ? projection?.OccurredAt.ToUniversalTime().ToString("O") : null,
            };
            var observation = new FindingObservation(
                fingerprint,
                finding["ruleId"]?.GetValue<string>() ?? string.Empty,
                finding["locations"]?.AsArray().FirstOrDefault()?["path"]?.GetValue<string>() ?? string.Empty,
                result["kind"]?.GetValue<string>() ?? string.Empty,
                finding["origin"]?["kind"]?.GetValue<string>() ?? (finding["source"] is null ? "agent" : "deterministic"),
                finding["id"]?.GetValue<string>() ?? string.Empty,
                finding["title"]?.GetValue<string>() ?? string.Empty);
            var rule = FindingSuppressionStore.Match(snapshot.Suppressions, observation, snapshot.At);
            finding["suppression"] = rule is null ? null : new JsonObject
            {
                ["ruleId"] = rule.Id,
                ["reason"] = rule.Reason,
                ["author"] = rule.Author,
                ["expiresAt"] = rule.ExpiresAt?.ToUniversalTime().ToString("O"),
            };
            if (rule is not null) suppressed++;
        }
        result["assessmentCounts"] = new JsonObject(assessmentCounts.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)));
        result["resolutionCounts"] = new JsonObject(resolutionCounts.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, pair.Value)));
        result["suppressionCounts"] = new JsonObject { ["suppressed"] = suppressed, ["visible"] = (result["findings"]?.AsArray().Count ?? 0) - suppressed };
        result["suppressionRevision"] = snapshot.Suppressions.Revision;
        return result;
    }

    private static string ToKebab(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
