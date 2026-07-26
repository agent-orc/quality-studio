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

        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var ruleId = finding["ruleId"]!.GetValue<string>().Trim();
            var locations = finding["locations"]!.AsArray().OfType<JsonObject>().ToArray();
            string? primaryPath = null;
            string? primarySnippet = null;
            foreach (var location in locations)
            {
                var path = NormalizePath(location["path"]!.GetValue<string>());
                if (!normalizedSubjects.TryGetValue(path, out var content))
                {
                    throw new ReviewResponseException($"Finding location '{path}' is not part of the reviewed subject.");
                }

                var range = location["range"]!.AsObject();
                var snippet = ExtractSnippet(content, path, range);
                location["path"] = path;
                primaryPath ??= path;
                primarySnippet ??= NormalizeSnippet(snippet);
            }

            var fingerprint = Compute(primaryPath!, primarySnippet!, ruleId);
            if (!fingerprints.Add(fingerprint))
            {
                throw new ReviewResponseException($"The agent returned duplicate finding identity '{fingerprint}'.");
            }

            var id = "finding-" + fingerprint[7..];
            finding["id"] = id;
            finding["ruleId"] = ruleId;
            finding["fingerprint"] = fingerprint;
            result.Add(new FindingIdentityRecord(fingerprint, id, primaryPath!, ruleId));
        }

        return result;
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
        var startLine = start["line"]!.GetValue<int>();
        var startColumn = start["column"]!.GetValue<int>();
        var endLine = end["line"]!.GetValue<int>();
        var endColumn = end["column"]!.GetValue<int>();
        var lines = content.Split('\n');
        if (startLine > lines.Length || endLine > lines.Length || endLine < startLine ||
            startColumn > lines[startLine - 1].Length + 1 || endColumn > lines[endLine - 1].Length + 1 ||
            (startLine == endLine && endColumn < startColumn))
        {
            throw new ReviewResponseException($"Finding range is outside reviewed file '{path}'.");
        }

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
