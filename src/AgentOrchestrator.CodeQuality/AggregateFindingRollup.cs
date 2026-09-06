using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Records a module or project finding that covers several files. `locations` has always been a
/// list, but only the first entry carried runner-measured evidence, so a rollup looked like a
/// single-site finding with extra prose. This adds a `related` anchor with its own captured
/// excerpt for every further location the runner can resolve, and turns the agent's citations of
/// member-file findings into evidence items whose status says whether the citation checks out.
/// It adds no property the review-meta v3 schema does not already define.
/// </summary>
public static class AggregateFindingRollup
{
    /// <summary>Agent-supplied member-finding fingerprints. Never persisted: the schema forbids it.</summary>
    public const string CitationProperty = "relatedFindings";

    private const int MaximumRelatedAnchors = 50;
    private const int MaximumCitations = 50;
    private const int MaximumExcerptCharacters = 2_000;

    public static void Apply(
        JsonObject response,
        ReviewLevel level,
        IReadOnlyDictionary<string, string> subjectContents,
        IReadOnlyDictionary<string, string> memberFindings)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(subjectContents);
        ArgumentNullException.ThrowIfNull(memberFindings);
        var normalized = subjectContents.ToDictionary(
            pair => pair.Key, pair => NormalizeLineEndings(pair.Value), StringComparer.Ordinal);
        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var citations = finding[CitationProperty];
            finding.Remove(CitationProperty);
            if (level == ReviewLevel.File) continue;
            if (finding["anchors"] is not JsonArray anchors ||
                finding["evidenceItems"] is not JsonArray evidenceItems)
            {
                // No runner-measured primary anchor: the finding did not resolve into the subject,
                // so there is nothing trustworthy to hang a rollup on.
                continue;
            }

            AppendRelatedAnchors(finding, anchors, evidenceItems, normalized);
            AppendCitations(evidenceItems, citations, memberFindings);
        }
    }

    private static void AppendRelatedAnchors(
        JsonObject finding,
        JsonArray anchors,
        JsonArray evidenceItems,
        IReadOnlyDictionary<string, string> contents)
    {
        var ordinal = 1;
        var primarySeen = false;
        foreach (var location in finding["locations"]!.AsArray().OfType<JsonObject>())
        {
            if (location["path"]?.GetValue<string>() is not { } path ||
                !contents.TryGetValue(path, out var content) ||
                location["range"] is not JsonObject range)
            {
                continue;
            }

            // FindingIdentity already anchored the first resolvable location as `primary`.
            if (!primarySeen)
            {
                primarySeen = true;
                continue;
            }

            if (++ordinal > MaximumRelatedAnchors) return;
            var anchorId = "related-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var excerpt = Excerpt(content, range);
            anchors.Add(new JsonObject
            {
                ["id"] = anchorId,
                ["role"] = "related",
                ["path"] = path,
                ["range"] = range.DeepClone(),
                ["capturedExcerpt"] = new JsonObject
                {
                    ["text"] = excerpt,
                    ["contentHash"] = Sha256(content),
                    ["excerptHash"] = Sha256(excerpt),
                },
            });
            evidenceItems.Add(new JsonObject
            {
                ["id"] = "ev-source-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["class"] = "sourceSpan",
                ["status"] = "observed",
                ["anchorId"] = anchorId,
            });
        }
    }

    /// <summary>
    /// A cited member finding is recorded as a claim, because the aggregate review did not measure
    /// it. Its status separates a citation the runner could match against a member sidecar from
    /// one it could not, so a fabricated fingerprint is visible instead of authoritative.
    /// </summary>
    private static void AppendCitations(
        JsonArray evidenceItems,
        JsonNode? citations,
        IReadOnlyDictionary<string, string> memberFindings)
    {
        if (citations is not JsonArray list) return;
        var ordinal = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var fingerprint)) continue;
            fingerprint = fingerprint.Trim();
            if (fingerprint.Length == 0 || !seen.Add(fingerprint)) continue;
            if (++ordinal > MaximumCitations) return;
            var known = memberFindings.TryGetValue(fingerprint, out var path);
            evidenceItems.Add(new JsonObject
            {
                ["id"] = "ev-member-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["class"] = "legacyClaim",
                ["status"] = known ? "observed" : "unverified",
                ["summary"] = known
                    ? $"Generalises member finding {fingerprint} in {path}."
                    : $"Cited member finding {fingerprint} was not found in the member sidecars of this aggregate.",
            });
        }
    }

    private static string Excerpt(string content, JsonObject range)
    {
        var lines = content.Split('\n');
        var startLine = Math.Clamp(range["start"]!["line"]!.GetValue<int>(), 1, lines.Length);
        var endLine = Math.Clamp(range["end"]!["line"]!.GetValue<int>(), startLine, lines.Length);
        var startColumn = Math.Clamp(range["start"]!["column"]!.GetValue<int>(), 1, lines[startLine - 1].Length + 1);
        var endColumn = Math.Clamp(range["end"]!["column"]!.GetValue<int>(), 1, lines[endLine - 1].Length + 1);
        var builder = new StringBuilder();
        if (startLine == endLine)
        {
            builder.Append(lines[startLine - 1][(startColumn - 1)..Math.Min(endColumn, lines[startLine - 1].Length)]);
        }
        else
        {
            builder.Append(lines[startLine - 1][(startColumn - 1)..]);
            for (var line = startLine; line < endLine - 1; line++) builder.Append('\n').Append(lines[line]);
            builder.Append('\n').Append(lines[endLine - 1][..Math.Min(endColumn, lines[endLine - 1].Length)]);
        }

        var excerpt = builder.ToString();
        return excerpt.Length <= MaximumExcerptCharacters ? excerpt : excerpt[..MaximumExcerptCharacters];
    }

    private static string Sha256(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
