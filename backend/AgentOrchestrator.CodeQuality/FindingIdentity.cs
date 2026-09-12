using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Assigns repository-stable identities to agent findings. The agent chooses the rule and
/// location, but never controls the persisted id or fingerprint.
/// </summary>
public static partial class FindingIdentity
{
    public const string Canonicalization = "quality-studio-finding-v1";

    public static IReadOnlyList<FindingIdentityRecord> Assign(
        JsonObject response,
        IReadOnlyDictionary<string, string> subjectContents)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(subjectContents);
        var normalizedSubjects = subjectContents.ToDictionary(
            pair => NormalizePath(pair.Key), pair => NormalizeLineEndings(pair.Value), StringComparer.Ordinal);
        var result = new List<FindingIdentityRecord>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new List<JsonObject>();

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
                duplicates.Add(finding);
                continue;
            }

            var fingerprint = Compute(primaryPath, primarySnippet!, ruleId);
            if (!fingerprints.Add(fingerprint))
            {
                // Duplicate identities collapse into one finding instead of discarding the
                // whole completed review document.
                duplicates.Add(finding);
                continue;
            }

            var id = "finding-" + fingerprint[7..];
            finding["id"] = id;
            finding["ruleId"] = ruleId;
            finding["fingerprint"] = fingerprint;
            if (primaryRange is not null)
            {
                AttachRunnerCapturedEvidence(finding, primaryPath!, primaryRange, primaryRawSnippet!, primaryContent!);
            }
            result.Add(new FindingIdentityRecord(fingerprint, id, primaryPath!, ruleId));
        }

        foreach (var duplicate in duplicates)
        {
            response["findings"]!.AsArray().Remove(duplicate);
        }

        return result;
    }

    /// <summary>
    /// Attaches source-span evidence the runner computed itself from the validated range above —
    /// the model never supplies <c>contentHash</c>/<c>excerptHash</c>, so these anchors stay trustworthy
    /// even though the review agent cannot verify a reproduction (see the "unknown" default below).
    /// </summary>
    private static void AttachRunnerCapturedEvidence(
        JsonObject finding, string primaryPath, JsonObject range, string excerptText, string fileContent)
    {
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fileContent)));
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

public sealed record FindingIdentityRecord(string Fingerprint, string Id, string Path, string RuleId);
