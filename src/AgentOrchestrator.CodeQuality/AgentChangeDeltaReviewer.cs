using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed class AgentChangeDeltaReviewer(IReviewAgent agent) : IChangeDeltaReviewer
{
    public async Task<ChangeJudgement> ReviewAsync(
        string repositoryRoot,
        ChangeSet changeSet,
        DeterministicChangeDelta delta,
        string diff,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(changeSet, delta, diff);
        var result = await agent.RunAsync(prompt, repositoryRoot, cancellationToken).ConfigureAwait(false);
        return Parse(result);
    }

    private static string BuildPrompt(ChangeSet changeSet, DeterministicChangeDelta delta, string diff)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        var evidence = JsonSerializer.Serialize(delta, jsonOptions);
        var builder = new StringBuilder();
        builder.AppendLine("Review this repository change as a delta. Do not review whole files and do not invent absolute grades.");
        builder.AppendLine("Use the deterministic evidence as fact and the unified diff as the only source-code evidence.");
        builder.AppendLine("Return exactly one JSON object with: summary, and aspects containing exactly four objects.");
        builder.AppendLine("Aspect ids must be risk, test-evidence, scope-discipline, architecture-drift.");
        builder.AppendLine("Each aspect requires id, title, verdict (good|mixed|concerning|unknown), and rationale.");
        builder.AppendLine();
        builder.AppendLine($"Change: {changeSet.BaseCommit}..{changeSet.ResultCommit} — {changeSet.Title}");
        builder.AppendLine("Deterministic evidence:");
        builder.AppendLine(evidence);
        builder.AppendLine("Unified diff:");
        builder.AppendLine(diff);
        return builder.ToString();
    }

    private ChangeJudgement Parse(ReviewAgentResult result)
    {
        JsonObject root;
        try
        {
            var response = result.Response.Trim();
            if (response.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = response.IndexOf('\n');
                var closing = response.LastIndexOf("```", StringComparison.Ordinal);
                response = firstNewline >= 0 && closing > firstNewline
                    ? response[(firstNewline + 1)..closing].Trim()
                    : response;
            }
            root = JsonNode.Parse(response)?.AsObject()
                   ?? throw new ChangeReviewException("The change reviewer returned no JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ChangeReviewException("The change reviewer returned invalid JSON.", exception);
        }

        var summary = Required(root, "summary");
        if (root["aspects"] is not JsonArray aspectArray)
            throw new ChangeReviewException("The change reviewer did not return aspects.");
        var aspects = aspectArray.OfType<JsonObject>().Select(aspect => new ChangeJudgementAspect(
            Required(aspect, "id"),
            Required(aspect, "title"),
            Required(aspect, "verdict"),
            Required(aspect, "rationale"))).ToArray();
        var ids = aspects.Select(aspect => aspect.Id).ToHashSet(StringComparer.Ordinal);
        var required = new[] { "risk", "test-evidence", "scope-discipline", "architecture-drift" };
        if (aspects.Length != 4 || required.Any(id => !ids.Contains(id)))
            throw new ChangeReviewException("The change reviewer must return exactly the four named aspects.");
        if (aspects.Any(aspect => aspect.Verdict is not ("good" or "mixed" or "concerning" or "unknown")))
            throw new ChangeReviewException("A change judgement verdict is unsupported.");

        var model = result.EffectiveModel ?? agent.Model ?? agent.AgentName;
        return new ChangeJudgement("complete", model, aspects, summary);
    }

    private static string Required(JsonObject value, string property) =>
        value[property]?.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new ChangeReviewException($"Change judgement property '{property}' is missing.");
}
