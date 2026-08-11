using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Adds evidence that only the trusted runner can assert after range validation.
/// Agent prose remains an explicitly unverified legacy claim.
/// </summary>
public static class FindingEvidence
{
    public static void Enrich(JsonObject response, IReadOnlyDictionary<string, string> subjectContents)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(subjectContents);
        var contents = subjectContents.ToDictionary(
            pair => NormalizePath(pair.Key), pair => NormalizeLineEndings(pair.Value), StringComparer.Ordinal);

        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            finding["impact"] ??= finding["description"]!.DeepClone();
            var evidence = new JsonArray();
            var locations = finding["locations"]!.AsArray().OfType<JsonObject>().ToArray();
            for (var index = 0; index < locations.Length; index++)
            {
                var location = locations[index];
                var path = NormalizePath(location["path"]!.GetValue<string>());
                location["role"] = index == 0 ? "primary" : "related";
                if (location["range"] is not JsonObject range || !contents.TryGetValue(path, out var content)) continue;

                var excerpt = FindingIdentity.ExtractValidatedSnippet(content, path, range);
                location["capturedExcerpt"] = new JsonObject
                {
                    ["text"] = excerpt,
                    ["language"] = Language(path),
                    ["contentHash"] = Hash(content),
                    ["excerptHash"] = Hash(excerpt),
                };
                evidence.Add(new JsonObject
                {
                    ["id"] = $"ev-source-{index + 1}",
                    ["class"] = "source-span",
                    ["status"] = "observed",
                    ["anchorIndex"] = index,
                    ["producer"] = "quality-studio-runner",
                    ["summary"] = "The runner captured this exact span after validating it against the reviewed subject.",
                    ["resultHash"] = Hash(excerpt),
                });
            }

            if (finding["source"] is JsonObject source)
            {
                evidence.Add(new JsonObject
                {
                    ["id"] = "ev-deterministic",
                    ["class"] = "deterministic-result",
                    ["status"] = "observed",
                    ["producer"] = source["producer"]?.GetValue<string>() ?? source["sensorId"]?.GetValue<string>(),
                    ["summary"] = $"Deterministic result from {source["sensorId"]?.GetValue<string>() ?? "sensor"}.",
                });
            }

            if (finding["evidence"] is JsonValue claim && claim.TryGetValue<string>(out var claimText) &&
                !string.IsNullOrWhiteSpace(claimText))
            {
                evidence.Add(new JsonObject
                {
                    ["id"] = "ev-legacy-claim",
                    ["class"] = "legacy-claim",
                    ["status"] = "unverified",
                    ["producer"] = "review-agent",
                    ["summary"] = claimText,
                });
            }
            finding["evidenceItems"] = evidence;

            if (finding["reproduction"] is not JsonObject reproduction)
            {
                reproduction = new JsonObject { ["status"] = "unknown", ["steps"] = new JsonArray() };
                finding["reproduction"] = reproduction;
            }
            reproduction["attempts"] = new JsonArray();
        }
    }

    public static string SourceRevision(string repositoryRoot)
    {
        var commit = Git(repositoryRoot, "rev-parse", "--verify", "HEAD");
        if (string.IsNullOrWhiteSpace(commit)) return "unknown";
        var dirty = !string.IsNullOrWhiteSpace(Git(repositoryRoot, "status", "--porcelain"));
        return $"git:{commit.Trim()}{(dirty ? "+uncommitted" : string.Empty)}";
    }

    private static string? Git(string repositoryRoot, params string[] arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repositoryRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0) return null;
            return output;
        }
        catch
        {
            return null;
        }
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".ts" or ".js" or ".mjs" => "typescript",
        ".html" => "html",
        ".css" or ".scss" => "css",
        ".json" => "json",
        ".md" => "markdown",
        _ => "text",
    };

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }
}
