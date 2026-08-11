using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public static partial class ReviewOutputRedactor
{
    private const string Marker = "[REDACTED CREDENTIAL]";

    public static int Redact(JsonObject response) => RedactNode(response);

    private static int RedactNode(JsonNode? node)
    {
        var changes = 0;
        if (node is JsonObject value)
        {
            foreach (var property in value.ToArray())
            {
                if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    var redacted = RedactText(text);
                    if (!string.Equals(text, redacted, StringComparison.Ordinal))
                    {
                        value[property.Key] = redacted;
                        changes++;
                    }
                }
                else
                {
                    changes += RedactNode(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    var redacted = RedactText(text);
                    if (!string.Equals(text, redacted, StringComparison.Ordinal))
                    {
                        array[index] = redacted;
                        changes++;
                    }
                }
                else
                {
                    changes += RedactNode(array[index]);
                }
            }
        }

        return changes;
    }

    private static string RedactText(string value)
    {
        try
        {
            var redacted = PrivateKey().Replace(value, Marker);
            redacted = BearerCredential().Replace(redacted, $"Bearer {Marker}");
            redacted = JsonWebToken().Replace(redacted, Marker);
            redacted = GitHubToken().Replace(redacted, Marker);
            redacted = AwsAccessKey().Replace(redacted, Marker);
            return CredentialAssignment().Replace(redacted, match =>
                $"{match.Groups[1].Value}{match.Groups[2].Value}{Marker}{match.Groups[4].Value}");
        }
        catch (RegexMatchTimeoutException)
        {
            return Marker;
        }
    }

    [GeneratedRegex(
        "-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----.*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex BearerCredential();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(
        @"\bgh[pousr]_[A-Za-z0-9]{20,}\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(
        @"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(
        @"\b(api[ _-]?key|access[ _-]?token|secret|password|authorization)(\s*[:=]\s*[\""']?)([A-Za-z0-9_./+=-]{8,})([\""']?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CredentialAssignment();
}
