using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Assigns repository-stable identities to agent findings. The agent chooses the rule and
/// location, but never controls the persisted id or fingerprint.
/// </summary>
/// <remarks>
/// A finding computes its fingerprint from rule, path and the code its range encloses. Across
/// reruns that alone is too brittle: an agent that re-reports the same defect one line wider
/// encloses different code, and the defect would get a new identity while the old one looked
/// fixed. So a finding first keeps an earlier identity with the same fingerprint, and otherwise
/// adopts the identity of an earlier finding for the same rule whose anchor span it overlaps on
/// content with the same hash. The agent's title and description never take part.
/// </remarks>
public static partial class FindingIdentity
{
    public const string Canonicalization = "quality-studio-finding-v1";

    public static IReadOnlyList<FindingIdentityRecord> Assign(
        JsonObject response,
        IReadOnlyDictionary<string, string> subjectContents,
        IReadOnlyCollection<FindingIdentityRecord>? earlier = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(subjectContents);
        var normalizedSubjects = subjectContents.ToDictionary(
            pair => NormalizePath(pair.Key), pair => NormalizeLineEndings(pair.Value), StringComparer.Ordinal);
        var contentHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        var dropped = new List<JsonObject>();

        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var ruleId = finding["ruleId"]!.GetValue<string>().Trim();
            var locations = finding["locations"]!.AsArray().OfType<JsonObject>().ToArray();
            string? primaryPath = null;
            string? primarySnippet = null;
            JsonObject? primaryRange = null;
            string? primaryRawSnippet = null;
            string? primaryContent = null;
            foreach (var location in locations)
            {
                var path = NormalizePath(location["path"]!.GetValue<string>());
                if (!normalizedSubjects.TryGetValue(path, out var content))
                {
                    // A location outside the reviewed subject (agents cite project files or
                    // neighbors) invalidates this location, not the whole review document.
                    continue;
                }

                var range = location["range"]!.AsObject();
                var snippet = ExtractSnippet(content, path, range);
                location["path"] = path;
                if (primaryPath is null)
                {
                    primaryPath = path;
                    primaryRange = range;
                    primaryRawSnippet = snippet;
                    primaryContent = content;
                }
                primarySnippet ??= NormalizeSnippet(snippet);
            }

            if (primaryPath is null)
            {
                // No location resolved into the reviewed subject: drop the finding, keep
                // the completed review document.
                dropped.Add(finding);
                continue;
            }

            if (!contentHashes.TryGetValue(primaryPath, out var contentHash))
                contentHashes[primaryPath] = contentHash = HashNormalized(primaryContent!);
            candidates.Add(new Candidate(finding, ruleId, primaryPath, primaryRange!, primaryRawSnippet!,
                contentHash, Compute(primaryPath, primarySnippet!, ruleId)));
        }

        MatchEarlier(candidates, earlier ?? []);

        var result = new List<FindingIdentityRecord>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var fingerprint = candidate.Earlier?.Fingerprint ?? candidate.ComputedFingerprint;
            if (!fingerprints.Add(fingerprint))
            {
                // Duplicate identities collapse into one finding instead of discarding the
                // whole completed review document.
                dropped.Add(candidate.Finding);
                continue;
            }

            var id = candidate.Earlier?.Id ?? "finding-" + fingerprint[7..];
            var finding = candidate.Finding;
            finding["id"] = id;
            finding["ruleId"] = candidate.RuleId;
            finding["fingerprint"] = fingerprint;
            AttachRunnerCapturedEvidence(finding, candidate.Path, candidate.Range, candidate.RawSnippet,
                candidate.ContentHash);
            result.Add(new FindingIdentityRecord(fingerprint, id, candidate.Path, candidate.RuleId,
                candidate.ContentHash, ReadRange(candidate.Range)));
        }

        foreach (var finding in dropped)
        {
            response["findings"]!.AsArray().Remove(finding);
        }

        return result;
    }

    /// <summary>
    /// Gives each candidate at most one earlier identity, and each earlier identity to at most one
    /// candidate. Exact fingerprints are settled first so an overlapping neighbour cannot take an
    /// identity that a candidate reproduces exactly. A span match needs the same rule, the same path,
    /// the same content hash — the code both anchors point into is identical — and overlapping lines;
    /// the largest overlap wins, then the nearest start line.
    /// </summary>
    private static void MatchEarlier(List<Candidate> candidates, IReadOnlyCollection<FindingIdentityRecord> earlier)
    {
        if (earlier.Count == 0) return;
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var byFingerprint = new Dictionary<string, FindingIdentityRecord>(StringComparer.Ordinal);
        foreach (var record in earlier) byFingerprint.TryAdd(record.Fingerprint, record);

        foreach (var candidate in candidates)
        {
            if (byFingerprint.TryGetValue(candidate.ComputedFingerprint, out var exact) && claimed.Add(exact.Fingerprint))
                candidate.Earlier = exact;
        }

        foreach (var candidate in candidates.Where(candidate => candidate.Earlier is null))
        {
            var range = ReadRange(candidate.Range);
            var match = earlier
                .Where(record => record.Range is not null &&
                                 !claimed.Contains(record.Fingerprint) &&
                                 string.Equals(record.RuleId, candidate.RuleId, StringComparison.Ordinal) &&
                                 string.Equals(record.Path, candidate.Path, StringComparison.Ordinal) &&
                                 string.Equals(record.ContentHash, candidate.ContentHash, StringComparison.Ordinal) &&
                                 OverlappingLines(record.Range, range) > 0)
                .OrderByDescending(record => OverlappingLines(record.Range!, range))
                .ThenBy(record => Math.Abs(record.Range!.Start.Line - range.Start.Line))
                .ThenBy(record => record.Fingerprint, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match is not null && claimed.Add(match.Fingerprint)) candidate.Earlier = match;
        }
    }

    private static int OverlappingLines(FindingRange? left, FindingRange right) =>
        left is null ? 0 : Math.Min(left.End.Line, right.End.Line) - Math.Max(left.Start.Line, right.Start.Line) + 1;

    private static FindingRange ReadRange(JsonObject range) => new(
        new FindingPosition(range["start"]!["line"]!.GetValue<int>(), range["start"]!["column"]!.GetValue<int>()),
        new FindingPosition(range["end"]!["line"]!.GetValue<int>(), range["end"]!["column"]!.GetValue<int>()));

    /// <summary>
    /// The anchor content hash for a subject file: SHA-256 over its text with line endings
    /// normalized to LF, the same value recorded in <c>capturedExcerpt.contentHash</c>.
    /// </summary>
    public static string ContentHash(string content) => HashNormalized(NormalizeLineEndings(content));

    private static string HashNormalized(string normalizedContent) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent)));

    private sealed class Candidate(
        JsonObject finding, string ruleId, string path, JsonObject range, string rawSnippet, string contentHash,
        string computedFingerprint)
    {
        public JsonObject Finding { get; } = finding;
        public string RuleId { get; } = ruleId;
        public string Path { get; } = path;
        public JsonObject Range { get; } = range;
        public string RawSnippet { get; } = rawSnippet;
        public string ContentHash { get; } = contentHash;
        public string ComputedFingerprint { get; } = computedFingerprint;
        public FindingIdentityRecord? Earlier { get; set; }
    }

    /// <summary>
    /// Attaches source-span evidence the runner computed itself from the validated range above —
    /// the model never supplies <c>contentHash</c>/<c>excerptHash</c>, so these anchors stay trustworthy
    /// even though the review agent cannot verify a reproduction (see the "unknown" default below).
    /// </summary>
    private static void AttachRunnerCapturedEvidence(
        JsonObject finding, string primaryPath, JsonObject range, string excerptText, string contentHash)
    {
        var excerptHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(excerptText)));
        finding["anchors"] = new JsonArray(new JsonObject
        {
            ["id"] = "primary",
            ["role"] = "primary",
            ["path"] = primaryPath,
            ["range"] = range.DeepClone(),
            ["capturedExcerpt"] = new JsonObject
            {
                ["text"] = excerptText,
                ["contentHash"] = contentHash,
                ["excerptHash"] = excerptHash,
            },
        });

        var evidenceItems = new JsonArray(new JsonObject
        {
            ["id"] = "ev-source",
            ["class"] = "sourceSpan",
            ["status"] = "observed",
            ["anchorId"] = "primary",
        });
        if (finding["evidence"]?.GetValue<string>() is { Length: > 0 } legacyEvidence)
        {
            evidenceItems.Add(new JsonObject
            {
                ["id"] = "ev-legacy",
                ["class"] = "legacyClaim",
                ["status"] = "unverified",
                ["summary"] = legacyEvidence,
            });
        }
        finding["evidenceItems"] = evidenceItems;

        // Prompts do not yet ask the agent for reproduction steps (they forbid tool/command use),
        // so this stays "unknown" rather than defaulting to a status the runner cannot back up.
        finding["reproduction"] = new JsonObject { ["status"] = "unknown" };
    }

    public static string Compute(string path, string normalizedSnippet, string ruleId)
    {
        var canonical = $"{Canonicalization}\0{NormalizePath(path)}\0{normalizedSnippet}\0{ruleId.Trim()}";
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string NormalizeSnippet(string snippet) =>
        Whitespace().Replace(NormalizeLineEndings(snippet).Trim(), " ");

    private static string ExtractSnippet(string content, string path, JsonObject range)
    {
        var start = range["start"]!.AsObject();
        var end = range["end"]!.AsObject();
        var lines = content.Split('\n');

        // Agents locate findings against unnumbered file content, so ranges arrive slightly
        // off. An out-of-bounds range is clamped to the file instead of discarding the whole
        // completed review document; the persisted range is rewritten to stay consistent
        // with the captured excerpt.
        var startLine = Math.Clamp(start["line"]!.GetValue<int>(), 1, lines.Length);
        var endLine = Math.Clamp(end["line"]!.GetValue<int>(), startLine, lines.Length);
        var startColumn = Math.Clamp(start["column"]!.GetValue<int>(), 1, lines[startLine - 1].Length + 1);
        var endColumn = Math.Clamp(end["column"]!.GetValue<int>(), 1, lines[endLine - 1].Length + 1);
        if (startLine == endLine && endColumn < startColumn)
        {
            (startColumn, endColumn) = (1, lines[startLine - 1].Length + 1);
        }
        start["line"] = startLine;
        start["column"] = startColumn;
        end["line"] = endLine;
        end["column"] = endColumn;

        if (startLine == endLine)
        {
            return lines[startLine - 1][(startColumn - 1)..Math.Min(endColumn, lines[startLine - 1].Length)];
        }

        var builder = new StringBuilder(lines[startLine - 1][(startColumn - 1)..]);
        for (var line = startLine; line < endLine - 1; line++) builder.Append('\n').Append(lines[line]);
        builder.Append('\n').Append(lines[endLine - 1][..Math.Min(endColumn, lines[endLine - 1].Length)]);
        return builder.ToString();
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.StartsWith("/", StringComparison.Ordinal) || path.Split('/').Any(segment => segment == ".."))
            throw new ReviewResponseException($"Finding path '{value}' must be repository-relative.");
        return path;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <summary>
/// One finding identity. <see cref="ContentHash"/> and <see cref="Range"/> are the primary anchor as
/// last observed — the content hash of its file and the span inside it — and are absent for findings
/// without a runner-measured anchor, such as deterministic sensor results.
/// </summary>
public sealed record FindingIdentityRecord(
    string Fingerprint,
    string Id,
    string Path,
    string RuleId,
    string? ContentHash = null,
    FindingRange? Range = null);
