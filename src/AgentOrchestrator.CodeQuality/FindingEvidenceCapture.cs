using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

internal static class FindingEvidenceCapture
{
    public static void EnrichFindings(
        JsonObject response,
        IReadOnlyDictionary<string, string> subjectContents,
        FindingOriginContext origin)
    {
        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var machineEvidence = ParseMachineEvidence(finding["evidence"]);
            var anchors = new JsonArray();
            var evidence = new JsonArray();
            var index = 0;
            foreach (var location in finding["locations"]!.AsArray().OfType<JsonObject>())
            {
                index++;
                var path = NormalizePath(location["path"]!.GetValue<string>());
                var content = NormalizeLineEndings(subjectContents[path]);
                var range = location["range"]!.AsObject();
                var excerpt = ExtractSnippet(content, range);
                var anchorId = index == 1 ? "primary" : $"related-{index - 1}";
                anchors.Add(new JsonObject
                {
                    ["id"] = anchorId,
                    ["role"] = index == 1 ? "primary" : "related",
                    ["path"] = path,
                    ["range"] = range.DeepClone(),
                    ["symbolId"] = location["symbolId"]?.DeepClone(),
                    ["capturedExcerpt"] = new JsonObject
                    {
                        ["text"] = excerpt,
                        ["language"] = Language(path),
                        ["contentHash"] = Hash(content),
                        ["excerptHash"] = Hash(excerpt),
                    },
                });
                evidence.Add(new JsonObject
                {
                    ["id"] = $"ev-source-{index}",
                    ["class"] = "source-span",
                    ["status"] = "observed",
                    ["anchorId"] = anchorId,
                    ["summary"] = "Runner-captured reviewed source span.",
                });
            }

            if (machineEvidence is not null)
            {
                var sensorId = machineEvidence["sensorId"]?.GetValue<string>() ?? "deterministic-sensor";
                evidence.Add(new JsonObject
                {
                    ["id"] = "ev-deterministic-result",
                    ["class"] = "deterministic-result",
                    ["status"] = "observed",
                    ["summary"] = $"Observed result from deterministic sensor {sensorId}.",
                    ["reference"] = machineEvidence["resultHash"]?.DeepClone(),
                });
                finding["source"] = new JsonObject
                {
                    ["kind"] = "deterministic",
                    ["sensorId"] = sensorId,
                    ["producer"] = sensorId,
                    ["producerVersion"] = machineEvidence["sensorVersion"]?.DeepClone(),
                };
            }
            else if (finding["evidence"] is JsonValue legacy && legacy.TryGetValue<string>(out var claim) &&
                !string.IsNullOrWhiteSpace(claim))
            {
                evidence.Add(new JsonObject
                {
                    ["id"] = "ev-legacy-claim",
                    ["class"] = "legacy-claim",
                    ["status"] = "claimed",
                    ["summary"] = claim,
                });
            }
            else if (finding["evidence"] is JsonArray supplied)
            {
                foreach (var item in supplied) evidence.Add(item?.DeepClone());
            }

            finding["problem"] = finding["description"]!.DeepClone();
            finding["impact"] ??= "Impact was not separately supplied by the reviewer.";
            finding["remediation"] = finding["recommendation"]!.DeepClone();
            finding["anchors"] = anchors;
            finding["evidence"] = evidence;
            finding["reproduction"] ??= new JsonObject
            {
                ["status"] = "unknown",
                ["steps"] = new JsonArray(),
                ["reason"] = "The reviewer supplied no reproduction contract.",
                ["attempts"] = new JsonArray(),
            };
            finding["origin"] = machineEvidence is null ? origin.ToJson() : DeterministicOrigin(machineEvidence, origin);
        }
    }

    private static JsonObject? ParseMachineEvidence(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var parsed = JsonNode.Parse(text)?.AsObject();
            return parsed?["source"]?.GetValue<string>() == "machine-sensor" ? parsed : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static JsonObject DeterministicOrigin(JsonObject machine, FindingOriginContext parent)
    {
        var sensorId = machine["sensorId"]?.GetValue<string>() ?? "deterministic-sensor";
        return new JsonObject
        {
            ["kind"] = "deterministic",
            ["reviewRunId"] = parent.ReviewRunId,
            ["operationRunId"] = $"sensor:{machine["resultHash"]?.GetValue<string>() ?? "unknown"}",
            ["requested"] = new JsonObject { ["model"] = null, ["thinkingLevel"] = null },
            ["executed"] = new JsonObject { ["cli"] = sensorId, ["model"] = "deterministic", ["thinkingLevel"] = null },
            ["prompt"] = new JsonObject { ["id"] = "deterministic-sensor", ["version"] = machine["sensorVersion"]?.DeepClone(), ["contentHash"] = machine["resultHash"]?.DeepClone() },
            ["reviewInputHash"] = parent.ReviewInputHash,
            ["subjectManifestHash"] = parent.SubjectManifestHash,
            ["sourceRevision"] = parent.SourceRevision,
            ["observedAt"] = parent.ObservedAt.ToUniversalTime().ToString("O"),
        };
    }

    private static string ExtractSnippet(string content, JsonObject range)
    {
        var start = range["start"]!.AsObject();
        var end = range["end"]!.AsObject();
        var startLine = start["line"]!.GetValue<int>();
        var startColumn = start["column"]!.GetValue<int>();
        var endLine = end["line"]!.GetValue<int>();
        var endColumn = end["column"]!.GetValue<int>();
        var lines = content.Split('\n');
        if (startLine == endLine) return lines[startLine - 1][(startColumn - 1)..Math.Min(endColumn, lines[startLine - 1].Length)];
        var builder = new StringBuilder(lines[startLine - 1][(startColumn - 1)..]);
        for (var line = startLine; line < endLine - 1; line++) builder.Append('\n').Append(lines[line]);
        return builder.Append('\n').Append(lines[endLine - 1][..Math.Min(endColumn, lines[endLine - 1].Length)]).ToString();
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string value)
    {
        var path = value.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path;
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp", ".ts" or ".tsx" => "typescript", ".js" or ".jsx" => "javascript",
        ".html" => "html", ".css" or ".scss" => "css", ".json" => "json", ".md" => "markdown", _ => "text",
    };
}

internal sealed record FindingOriginContext(
    string? ReviewRunId,
    string OperationRunId,
    string? RequestedModel,
    string? RequestedThinkingLevel,
    string ExecutedCli,
    string ExecutedModel,
    string? ExecutedThinkingLevel,
    string PromptId,
    string PromptVersion,
    string PromptHash,
    string ReviewInputHash,
    string SubjectManifestHash,
    string SourceRevision,
    DateTimeOffset ObservedAt)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = "agent",
        ["reviewRunId"] = ReviewRunId,
        ["operationRunId"] = OperationRunId,
        ["requested"] = new JsonObject { ["model"] = RequestedModel, ["thinkingLevel"] = RequestedThinkingLevel },
        ["executed"] = new JsonObject { ["cli"] = ExecutedCli, ["model"] = ExecutedModel, ["thinkingLevel"] = ExecutedThinkingLevel },
        ["prompt"] = new JsonObject { ["id"] = PromptId, ["version"] = PromptVersion, ["contentHash"] = PromptHash },
        ["reviewInputHash"] = ReviewInputHash,
        ["subjectManifestHash"] = SubjectManifestHash,
        ["sourceRevision"] = SourceRevision,
        ["observedAt"] = ObservedAt.ToUniversalTime().ToString("O"),
    };
}
