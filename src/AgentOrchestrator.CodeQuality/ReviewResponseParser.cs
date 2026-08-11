using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed partial class ReviewResponseParser
{
    public JsonObject Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ReviewResponseException("The agent returned no review response.");
        }

        var matches = JsonFence().Matches(response);
        if (matches.Count > 1)
        {
            throw new ReviewResponseException("The agent returned more than one JSON block.");
        }

        var json = matches.Count == 1 ? matches[0].Groups[1].Value : response.Trim();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new ReviewResponseException("The response root must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ReviewResponseException("The agent returned invalid JSON.", exception);
        }

        var grade = RequireObject(root, "grade");
        RejectUnknown(grade, "grade", "score", "band", "rationale");
        ValidateGrade(grade);
        RequireString(root, "summary");
        var aspects = RequireArray(root, "aspects");
        var findings = RequireArray(root, "findings");
        if (aspects.Count == 0)
        {
            throw new ReviewResponseException("At least one review aspect is required.");
        }

        var aspectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var aspectNode in aspects)
        {
            var aspect = aspectNode?.AsObject() ?? throw Invalid("aspect");
            RejectUnknown(aspect, "aspect", "id", "title", "grade");
            var id = RequireString(aspect, "id");
            RequireString(aspect, "title");
            var aspectGrade = RequireObject(aspect, "grade");
            RejectUnknown(aspectGrade, "aspect.grade", "score", "band", "rationale");
            ValidateGrade(aspectGrade);
            if (!aspectIds.Add(id))
            {
                throw new ReviewResponseException($"Duplicate review aspect '{id}'.");
            }
        }

        foreach (var findingNode in findings)
        {
            var finding = findingNode?.AsObject() ?? throw Invalid("finding");
            RejectUnknown(finding, "finding", "id", "fingerprint", "ruleId", "aspect", "severity", "title",
                "description", "impact", "recommendation", "locations", "evidence", "reproduction", "source",
                "origin", "anchors");
            if (finding.ContainsKey("source") || finding.ContainsKey("origin") || finding.ContainsKey("anchors"))
            {
                throw new ReviewResponseException(
                    "Agent-authored findings cannot claim deterministic source provenance, runner-captured anchors, or execution provenance.");
            }
            foreach (var property in new[] { "id", "ruleId", "aspect", "severity", "title", "description", "recommendation" })
            {
                RequireString(finding, property);
            }
            if (finding["ruleId"]!.GetValue<string>().Length > 200)
            {
                throw Invalid("ruleId");
            }

            var aspect = finding["aspect"]!.GetValue<string>();
            if (!aspectIds.Contains(aspect))
            {
                throw new ReviewResponseException($"Finding references unknown aspect '{aspect}'.");
            }

            var severity = finding["severity"]!.GetValue<string>();
            if (severity is not ("critical" or "high" or "medium" or "low" or "info"))
            {
                throw new ReviewResponseException($"Unsupported finding severity '{severity}'.");
            }

            var locations = RequireArray(finding, "locations");
            if (locations.Count == 0)
            {
                throw new ReviewResponseException("File findings require at least one location.");
            }

            foreach (var locationNode in locations)
            {
                var location = locationNode?.AsObject() ?? throw Invalid("location");
                RejectUnknown(location, "location", "path", "range", "symbolId");
                RequireString(location, "path");
                var range = RequireObject(location, "range");
                RejectUnknown(range, "range", "start", "end");
                var start = RequireObject(range, "start");
                var end = RequireObject(range, "end");
                RejectUnknown(start, "position", "line", "column");
                RejectUnknown(end, "position", "line", "column");
                ValidatePosition(start);
                ValidatePosition(end);
            }

            ValidateAgentEvidence(finding);
            ValidateReproduction(finding);

            if (finding["fingerprint"] is JsonValue fingerprintNode &&
                (!fingerprintNode.TryGetValue<string>(out var fingerprint) ||
                 fingerprint.Length != 71 || !fingerprint.StartsWith("sha256:", StringComparison.Ordinal) ||
                 !fingerprint[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw Invalid("fingerprint");
            }
        }

        if (root["threadUpdates"] is JsonArray updates)
        {
            var threadIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var updateNode in updates)
            {
                var update = updateNode?.AsObject() ?? throw Invalid("threadUpdate");
                var threadId = RequireString(update, "threadId");
                var body = RequireString(update, "body");
                if (body.Length > 20000) throw Invalid("body");
                if (update.ContainsKey("replyTo")) RequireString(update, "replyTo");
                if (!threadIds.Add(threadId)) throw new ReviewResponseException($"Duplicate thread update '{threadId}'.");
                if (update["status"] is JsonValue statusNode &&
                    (!statusNode.TryGetValue<string>(out var status) || status is not ("open" or "resolved")))
                    throw Invalid("status");
            }
        }

        return root;
    }

    private static void ValidateGrade(JsonObject grade)
    {
        if (grade["score"] is not JsonValue scoreNode || !scoreNode.TryGetValue<int>(out var score) || score is < 0 or > 100)
        {
            throw Invalid("score");
        }

        var band = RequireString(grade, "band");
        var expectedBand = score switch
        {
            >= 90 => "A",
            >= 80 => "B",
            >= 70 => "C",
            >= 60 => "D",
            _ => "F",
        };
        if (!string.Equals(band, expectedBand, StringComparison.Ordinal))
        {
            throw new ReviewResponseException($"Grade score {score} does not match band '{band}'.");
        }

        RequireString(grade, "rationale");
    }

    private static void ValidatePosition(JsonObject position)
    {
        foreach (var property in new[] { "line", "column" })
        {
            if (position[property] is not JsonValue node || !node.TryGetValue<int>(out var value) || value < 1)
            {
                throw Invalid(property);
            }
        }
    }

    private static void ValidateAgentEvidence(JsonObject finding)
    {
        if (finding["evidence"] is null) return;
        if (finding["evidence"] is JsonValue legacy && legacy.TryGetValue<string>(out var legacyText))
        {
            if (string.IsNullOrWhiteSpace(legacyText) || legacyText.Length > 20_000) throw Invalid("evidence");
            return;
        }
        if (finding["evidence"] is not JsonArray evidence) throw Invalid("evidence");
        foreach (var itemNode in evidence)
        {
            var item = itemNode?.AsObject() ?? throw Invalid("evidence");
            RejectUnknown(item, "evidence", "id", "class", "status", "summary", "reference");
            var evidenceClass = RequireString(item, "class");
            if (evidenceClass != "external-reference")
                throw new ReviewResponseException(
                    $"Agent-authored findings cannot claim '{evidenceClass}' evidence; trusted execution and source evidence is runner-owned.");
            if (RequireString(item, "status") != "claimed") throw Invalid("evidence.status");
            if (RequireString(item, "summary").Length > 4_000) throw Invalid("evidence.summary");
            RequireString(item, "reference");
        }
    }

    private static void ValidateReproduction(JsonObject finding)
    {
        if (finding["reproduction"] is null) return;
        var reproduction = RequireObject(finding, "reproduction");
        RejectUnknown(reproduction, "reproduction", "status", "steps", "expected", "observed", "reason", "attempts");
        var status = RequireString(reproduction, "status");
        if (status == "verified")
            throw new ReviewResponseException("Agent-authored findings cannot claim verified reproduction.");
        if (status is not ("specified" or "not-applicable" or "blocked" or "unknown"))
            throw Invalid("reproduction.status");
        if (reproduction["steps"] is not JsonArray steps || steps.Any(step =>
                step is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)))
            throw Invalid("reproduction.steps");
        if (reproduction["attempts"] is JsonArray attempts && attempts.Count > 0)
            throw new ReviewResponseException("Agent-authored findings cannot claim retained reproduction attempts.");
        if (status == "specified" && steps.Count == 0) throw Invalid("reproduction.steps");
        if (status == "not-applicable" &&
            (reproduction["reason"] is not JsonValue reason || !reason.TryGetValue<string>(out var reasonText) || string.IsNullOrWhiteSpace(reasonText)))
            throw Invalid("reproduction.reason");
    }

    private static JsonObject RequireObject(JsonObject value, string name) =>
        value[name] as JsonObject ?? throw Invalid(name);

    private static void RejectUnknown(JsonObject value, string name, params string[] allowed)
    {
        var fields = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = value.Select(property => property.Key).FirstOrDefault(property => !fields.Contains(property));
        if (unknown is not null) throw new ReviewResponseException($"Unexpected {name} property '{unknown}'.");
    }

    private static JsonArray RequireArray(JsonObject value, string name) =>
        value[name] as JsonArray ?? throw Invalid(name);

    private static string RequireString(JsonObject value, string name)
    {
        if (value[name] is not JsonValue node || !node.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(name);
        }

        return text;
    }

    private static ReviewResponseException Invalid(string property) =>
        new($"Review response property '{property}' is missing or invalid.");

    [GeneratedRegex(@"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonFence();
}

public sealed class ReviewResponseException(string message, Exception? innerException = null)
    : Exception(message, innerException);
