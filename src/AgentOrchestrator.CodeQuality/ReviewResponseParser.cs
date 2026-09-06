using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// The rule and guideline ids a review may cite, resolved for the unit under review. An agent that
/// names anything else — an invented rule, a rule for another technology, a guideline the budget
/// omitted — has its <c>ruleId</c> replaced with the base-criteria id rather than the review being
/// rejected. That replacement is what keeps finding fingerprints stable: the fingerprint covers the
/// rule id, so an unchecked invented id would give the same defect a new identity on every run.
/// </summary>
public sealed class RuleIdPolicy
{
    private readonly Dictionary<string, string> canonical;

    public RuleIdPolicy(IEnumerable<string> knownIds, string kind)
    {
        ArgumentNullException.ThrowIfNull(knownIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
        Fallback = "built-in:" + kind;
        canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in knownIds) canonical[id] = id;
        canonical[Fallback] = Fallback;
    }

    public string Kind { get; }

    public string Fallback { get; }

    /// <summary>
    /// Returns the catalogue's own spelling of <paramref name="ruleId"/>, or null when no input
    /// carries that id. Matching ignores case so a lowercased citation still resolves to the rule.
    /// </summary>
    public string? Canonicalize(string ruleId) =>
        canonical.TryGetValue(ruleId.Trim(), out var known) ? known : null;
}

public sealed partial class ReviewResponseParser
{
    public JsonObject Parse(string response) => Parse(response, null);

    public JsonObject Parse(string response, RuleIdPolicy? rules)
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

        ValidateGrade(RequireObject(root, "grade"));
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
            var id = CanonicalAspectId(RequireString(aspect, "id"));
            aspect["id"] = id;
            RequireString(aspect, "title");
            ValidateGrade(RequireObject(aspect, "grade"));
            if (!aspectIds.Add(id))
            {
                throw new ReviewResponseException($"Duplicate review aspect '{id}'.");
            }
        }

        foreach (var findingNode in findings)
        {
            var finding = findingNode?.AsObject() ?? throw Invalid("finding");
            if (finding.ContainsKey("source"))
            {
                throw new ReviewResponseException(
                    "Agent-authored findings cannot claim deterministic source provenance.");
            }
            foreach (var property in new[] { "id", "ruleId", "aspect", "severity", "title", "description", "recommendation" })
            {
                RequireString(finding, property);
            }
            var ruleId = finding["ruleId"]!.GetValue<string>();
            if (ruleId.Length > 200)
            {
                throw Invalid("ruleId");
            }

            if (rules is not null)
            {
                var known = rules.Canonicalize(ruleId);
                if (known is null)
                {
                    QualityStudioEventSource.Log.RuleIdRejected(ruleId, rules.Kind, rules.Fallback);
                    known = rules.Fallback;
                }
                finding["ruleId"] = known;
            }

            var aspect = CanonicalAspectId(finding["aspect"]!.GetValue<string>());
            finding["aspect"] = aspect;
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
                RequireString(location, "path");
                var range = RequireObject(location, "range");
                ValidatePosition(RequireObject(range, "start"));
                ValidatePosition(RequireObject(range, "end"));
            }

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

    /// <summary>
    /// Maps an agent-supplied aspect id onto the single spelling the review-meta schema allows.
    /// Trims, converts camelCase and PascalCase boundaries plus underscores and whitespace to
    /// single hyphens, lowercases, and rejects anything that still fails the schema pattern.
    /// </summary>
    internal static string CanonicalAspectId(string raw)
    {
        var text = raw.Trim();
        var builder = new StringBuilder(text.Length + 8);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '_' || char.IsWhiteSpace(character))
            {
                builder.Append('-');
                continue;
            }

            if (char.IsUpper(character))
            {
                var previous = index > 0 ? text[index - 1] : '\0';
                var next = index + 1 < text.Length ? text[index + 1] : '\0';
                var boundary = char.IsLower(previous) || char.IsDigit(previous) ||
                               (char.IsUpper(previous) && char.IsLower(next));
                if (boundary && builder.Length > 0 && builder[^1] != '-') builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            builder.Append(character);
        }

        var canonical = HyphenRun().Replace(builder.ToString(), "-").Trim('-', '.');
        if (!AspectId().IsMatch(canonical))
        {
            throw new ReviewResponseException(
                $"Review aspect id '{raw}' cannot be normalized to the schema pattern '^[a-z][a-z0-9.-]{{1,63}}$'.");
        }

        return canonical;
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

    private static JsonObject RequireObject(JsonObject value, string name) =>
        value[name] as JsonObject ?? throw Invalid(name);

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

    [GeneratedRegex("^[a-z][a-z0-9.-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AspectId();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenRun();

    [GeneratedRegex(@"```json\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonFence();
}

public sealed class ReviewResponseException(string message, Exception? innerException = null)
    : Exception(message, innerException);
