using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Reads the first complete JSON object out of an agent answer. A regex over the markdown fence
/// cannot do this: a lazy match stops at the first fence marker, and a finding whose recommendation
/// quotes a fenced snippet puts one inside a JSON string. The reader instead walks the text from an
/// opening brace, treating everything inside a JSON string as data, until that brace closes; prose,
/// a closing fence or a second block after the object is ignored. A <c>```json</c> fence is only a
/// hint where to start, so an answer without one is read the same way.
/// </summary>
public static class AgentJsonReader
{
    // Each failed candidate is skipped whole, so the bound only matters for answers full of
    // unrelated balanced braces in prose.
    private const int MaxCandidates = 64;

    public static JsonObject FirstObject(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new ReviewResponseException("The agent returned no response.");

        var fence = response.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        var start = fence < 0 ? response.IndexOf('{') : response.IndexOf('{', fence);
        if (start < 0 && fence >= 0) start = response.IndexOf('{');
        Exception? firstError = null;
        for (var attempt = 0; start >= 0 && attempt < MaxCandidates; attempt++)
        {
            var end = ClosingBrace(response, start);
            if (end < 0)
            {
                // The answer stopped before the object closed. Reading a nested object out of the
                // truncated one would only trade this precise error for a misleading schema error.
                throw new ReviewResponseException(
                    firstError is null
                        ? "The agent returned an incomplete JSON object; the response ended before it closed."
                        : $"The agent returned invalid JSON: {firstError.Message}",
                    firstError);
            }

            try
            {
                if (JsonNode.Parse(response.AsSpan(start, end - start + 1).ToString()) is JsonObject root)
                    return root;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // InvalidOperationException is how JsonObject rejects a duplicate property name.
                firstError ??= exception;
            }

            start = response.IndexOf('{', end + 1);
        }

        throw firstError is null
            ? new ReviewResponseException("The agent returned no JSON object.")
            : new ReviewResponseException($"The agent returned invalid JSON: {firstError.Message}", firstError);
    }

    /// <summary>
    /// The index of the brace that closes the object opened at <paramref name="start"/>, or -1 when
    /// the text ends first. Braces inside JSON strings, including escaped quotes, do not count.
    /// </summary>
    private static int ClosingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (character == '\\') index++;
                else if (character == '"') inString = false;
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return index;
                    break;
            }
        }

        return -1;
    }
}
