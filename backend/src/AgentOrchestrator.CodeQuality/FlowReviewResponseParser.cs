using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

internal sealed partial class FlowReviewResponseParser
{
    private static readonly HashSet<string> Classes =
    [
        "sessionLifecycle",
        "horizontalPrivilegeEscalation",
        "verticalPrivilegeEscalation",
        "objectOwnership",
        "flowBypass",
        "replay",
        "raceCondition",
        "quotaAbuse",
        "unenforcedInvariant",
    ];

    private static readonly HashSet<string> Stages =
    [
        "entry", "authentication", "authorization", "stateTransition", "persistence", "response", "external",
    ];

    public JsonObject Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new ReviewResponseException("The agent returned no flow review response.");
        var matches = JsonFence().Matches(response);
        if (matches.Count > 1)
            throw new ReviewResponseException("The agent returned more than one JSON block.");
        var json = matches.Count == 1 ? matches[0].Groups[1].Value : response.Trim();
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject()
                ?? throw new ReviewResponseException("The flow review response must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ReviewResponseException("The agent returned invalid flow review JSON.", exception);
        }

        var verdict = RequireString(root, "verdict");
        if (verdict is not ("pass" or "fail" or "undetermined"))
            throw Invalid("verdict");
        RequireString(root, "summary");
        var findings = RequireArray(root, "findings");
        var reason = OptionalString(root, "undeterminedReason");
        if (verdict == "pass" && findings.Count != 0)
            throw new ReviewResponseException("A passing flow review cannot contain findings.");
        if (verdict == "fail" && findings.Count == 0)
            throw new ReviewResponseException("A failing flow review must contain at least one finding.");
        if (verdict == "undetermined" && string.IsNullOrWhiteSpace(reason))
            throw new ReviewResponseException("An undetermined flow review must state the reason.");
        if (verdict != "undetermined" && reason is not null)
            throw new ReviewResponseException("Only an undetermined flow review can contain undeterminedReason.");

        foreach (var node in findings)
        {
            var finding = node?.AsObject() ?? throw Invalid("finding");
            foreach (var name in new[] { "class", "severity", "title", "description", "recommendation" })
                RequireString(finding, name);
            if (!Classes.Contains(finding["class"]!.GetValue<string>()))
                throw Invalid("class");
            if (finding["severity"]!.GetValue<string>() is not ("critical" or "high" or "medium" or "low" or "info"))
                throw Invalid("severity");
            if (finding["weakestPointIndex"] is not JsonValue weakestNode ||
                !weakestNode.TryGetValue<int>(out var weakestPointIndex))
                throw Invalid("weakestPointIndex");

            var path = RequireArray(finding, "flowPath");
            if (path.Count < 2)
                throw new ReviewResponseException("Every flow finding must record the full path, with at least entry and outcome steps.");
            if (weakestPointIndex < 0 || weakestPointIndex >= path.Count)
                throw new ReviewResponseException("A flow finding's weakestPointIndex is outside its flowPath.");
            for (var index = 0; index < path.Count; index++)
            {
                var step = path[index]?.AsObject() ?? throw Invalid("flowPath");
                if (step["order"] is not JsonValue orderNode || !orderNode.TryGetValue<int>(out var order) || order != index)
                    throw new ReviewResponseException("Flow path order must be contiguous and zero-based.");
                var stage = RequireString(step, "stage");
                if (!Stages.Contains(stage)) throw Invalid("stage");
                RequireString(step, "path");
                if (step["line"] is not JsonValue lineNode || !lineNode.TryGetValue<int>(out var line) || line < 1)
                    throw Invalid("line");
                RequireString(step, "symbol");
                RequireString(step, "action");
            }
            var firstStage = path[0]!["stage"]!.GetValue<string>();
            var lastStage = path[^1]!["stage"]!.GetValue<string>();
            if (firstStage != "entry" || lastStage is not ("response" or "external"))
                throw new ReviewResponseException("A finding's flow path must start at entry and end at response or an external dependency.");
        }
        return root;
    }

    private static JsonArray RequireArray(JsonObject value, string name) =>
        value[name] as JsonArray ?? throw Invalid(name);

    private static string RequireString(JsonObject value, string name)
    {
        if (value[name] is not JsonValue node || !node.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
            throw Invalid(name);
        return text.Trim();
    }

    private static string? OptionalString(JsonObject value, string name)
    {
        if (value[name] is null) return null;
        return RequireString(value, name);
    }

    private static ReviewResponseException Invalid(string property) =>
        new($"Flow review response property '{property}' is missing or invalid.");

    [GeneratedRegex(@"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonFence();
}
