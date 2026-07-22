using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record FindingStateCounts(int Open, int Accepted, int Waived, int FalsePositive, int Resolved)
{
    public int Visible => Open + Accepted + Waived + FalsePositive;
    public static FindingStateCounts Empty { get; } = new(0, 0, 0, 0, 0);
    public static FindingStateCounts operator +(FindingStateCounts left, FindingStateCounts right) =>
        new(left.Open + right.Open, left.Accepted + right.Accepted, left.Waived + right.Waived,
            left.FalsePositive + right.FalsePositive, left.Resolved + right.Resolved);
}

public static class FindingStateProjection
{
    public static JsonObject Apply(JsonObject metadata, IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var result = metadata.DeepClone().AsObject();
        var counts = FindingStateCounts.Empty;
        var includedWeight = 0;
        var excludedWeight = 0;
        foreach (var finding in result["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var fingerprint = finding["fingerprint"]?.GetValue<string>();
            var state = fingerprint is not null && states.TryGetValue(fingerprint, out var stored)
                ? stored
                : null;
            var effective = state?.State ?? FindingState.Open;
            finding["state"] = FindingStateStore.StateName(effective);
            if (state is not null)
            {
                finding["stateAuthor"] = state.Author;
                finding["stateReason"] = state.Reason;
                finding["stateTimestamp"] = state.Timestamp.ToUniversalTime().ToString("O");
                if (state.ExpiresAt is not null) finding["stateExpiresAt"] = state.ExpiresAt.Value.ToUniversalTime().ToString("O");
            }

            counts = Add(counts, effective);
            var weight = SeverityWeight(finding["severity"]?.GetValue<string>());
            if (effective is FindingState.Waived or FindingState.FalsePositive or FindingState.Resolved) excludedWeight += weight;
            else includedWeight += weight;
        }

        result["findingCounts"] = CountsJson(counts);
        ApplyEffectiveGrade(result, includedWeight, excludedWeight);
        return result;
    }

    public static FindingStateCounts Count(JsonObject metadata, IReadOnlyDictionary<string, FindingStateRecord> states)
    {
        var projected = Apply(metadata, states);
        var counts = projected["findingCounts"]!.AsObject();
        return new(counts["open"]!.GetValue<int>(), counts["accepted"]!.GetValue<int>(),
            counts["waived"]!.GetValue<int>(), counts["falsePositive"]!.GetValue<int>(), counts["resolved"]!.GetValue<int>());
    }

    private static void ApplyEffectiveGrade(JsonObject metadata, int includedWeight, int excludedWeight)
    {
        if (excludedWeight == 0 || metadata["grade"] is not JsonObject grade || grade["score"] is not JsonValue scoreNode ||
            !scoreNode.TryGetValue<int>(out var rawScore)) return;
        var totalWeight = includedWeight + excludedWeight;
        var adjusted = totalWeight == 0
            ? 100
            : (int)Math.Round(100 - (100 - rawScore) * (includedWeight / (double)totalWeight), MidpointRounding.AwayFromZero);
        adjusted = Math.Clamp(adjusted, rawScore, 100);
        grade["score"] = adjusted;
        grade["band"] = adjusted switch { >= 90 => "A", >= 80 => "B", >= 70 => "C", >= 60 => "D", _ => "F" };
        grade["rationale"] = grade["rationale"]!.GetValue<string>() +
            " Waived, false-positive, and resolved findings are excluded from this effective grade.";
    }

    private static int SeverityWeight(string? severity) => severity switch
    {
        "critical" => 16,
        "high" => 8,
        "medium" => 4,
        "low" => 2,
        _ => 1,
    };

    private static FindingStateCounts Add(FindingStateCounts counts, FindingState state) => state switch
    {
        FindingState.Accepted => counts with { Accepted = counts.Accepted + 1 },
        FindingState.Waived => counts with { Waived = counts.Waived + 1 },
        FindingState.FalsePositive => counts with { FalsePositive = counts.FalsePositive + 1 },
        FindingState.Resolved => counts with { Resolved = counts.Resolved + 1 },
        _ => counts with { Open = counts.Open + 1 },
    };

    private static JsonObject CountsJson(FindingStateCounts counts) => new()
    {
        ["open"] = counts.Open,
        ["accepted"] = counts.Accepted,
        ["waived"] = counts.Waived,
        ["falsePositive"] = counts.FalsePositive,
        ["resolved"] = counts.Resolved,
    };
}
