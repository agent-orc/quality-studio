using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure, deterministic projections from every legacy verdict/state spelling onto the
/// <c>quality-studio/core@1.0.0</c> axes (dossier docs/operations/data-model-taxonomy T1, section 8
/// "Compatibility mappings"). These are adapters, not rewrites: legacy files stay untouched, and every
/// migrated observation is expected to retain <c>legacy.schema</c>/<c>legacy.value</c> at the call site.
/// This library performs no I/O and does not run during any existing write path.
/// </summary>
public static class LegacyTaxonomyMapper
{
    public const string SecurityCombinationRule = "security-sensor-agent-v1";
    public const string AcceptedRiskLegacyAlias = "accepted";
    public const string FalsePositiveCamelCaseAlias = "falsePositive";

    public sealed record SecurityVerdictMapping(string Assessment, string EvidenceStatus, string? Decision, string PolicyRef);

    /// <summary>Section 8, rows "Security verdict".</summary>
    public static SecurityVerdictMapping MapSecurityVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => new SecurityVerdictMapping("pass", "available", "allow", SecurityCombinationRule),
        "warn" => new SecurityVerdictMapping("concern", "available", null, SecurityCombinationRule),
        "block" => new SecurityVerdictMapping("fail", "available", "block", SecurityCombinationRule),
        "unavailable" => new SecurityVerdictMapping("inconclusive", "unavailable", null, SecurityCombinationRule),
        _ => throw Unmapped("security verdict", legacyVerdict),
    };

    /// <summary>Section 8, row "Flow verdict".</summary>
    public static string MapFlowVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => "pass",
        "fail" => "fail",
        "undetermined" => "inconclusive",
        _ => throw Unmapped("flow verdict", legacyVerdict),
    };

    /// <summary>Section 8, row "Attack verdict".</summary>
    public static string MapAttackVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => "pass",
        "finding" => "fail",
        "not-applicable" => "not-applicable",
        "not-yet-checked" => "not-assessed",
        _ => throw Unmapped("attack verdict", legacyVerdict),
    };

    /// <summary>Section 8, row "Change summary" (the <c>change</c> axis).</summary>
    public static string MapChangeSummary(string legacySummary) => legacySummary switch
    {
        "no-quality-delta" => "no-observed-delta",
        "improved" => "improved",
        "neutral" => "unchanged",
        "regression" => "regressed",
        _ => throw Unmapped("change summary", legacySummary),
    };

    /// <summary>Section 8, row "Change aspect" (the <c>assessment</c> axis).</summary>
    public static string MapChangeAspectVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "good" => "pass",
        "mixed" => "concern",
        "concerning" => "fail",
        "unknown" => "inconclusive",
        _ => throw Unmapped("change aspect verdict", legacyVerdict),
    };

    /// <summary>Section 8, rows "Finding state". <c>accepted</c> and both false-positive spellings are read aliases.</summary>
    public static string MapFindingState(string legacyState) => legacyState switch
    {
        "open" => "open",
        "accepted" => "accepted-risk",
        "waived" => "waived",
        "false-positive" => "false-positive",
        "falsePositive" => "false-positive",
        "resolved" => "resolved",
        _ => throw Unmapped("finding state", legacyState),
    };

    public sealed record MappedEvidence(string Kind, string? MediaType, string Summary, string PreservedText, string ContentHash);

    /// <summary>
    /// Section 8, rows "Evidence string". Valid JSON becomes typed <c>tool-result</c> evidence; anything
    /// else is preserved as a typed evidence item with a summary. Malformed or unknown evidence is never
    /// discarded (migration invariant, section 11).
    /// </summary>
    public static MappedEvidence MapEvidenceString(string legacyEvidence)
    {
        ArgumentNullException.ThrowIfNull(legacyEvidence);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyEvidence)));

        if (IsValidJson(legacyEvidence))
        {
            return new MappedEvidence("tool-result", "application/json", "Legacy machine evidence payload.", legacyEvidence, digest);
        }

        var summary = legacyEvidence.Length <= 500 ? legacyEvidence : legacyEvidence[..500];
        return new MappedEvidence("document", null, summary, legacyEvidence, digest);
    }

    private static bool IsValidJson(string value)
    {
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ArgumentOutOfRangeException Unmapped(string axisName, string legacyValue) =>
        new(nameof(legacyValue), legacyValue, $"No quality-studio/core mapping is defined for legacy {axisName} '{legacyValue}'.");
}
