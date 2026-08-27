using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure adapters from every legacy verdict/state spelling in the dossier's compatibility
/// mapping table (docs/operations/data-model-taxonomy/index.html, section 8) to the core v1
/// taxonomy. These do not read or write any file; T5 wires them into the migration tool.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public const string SecuritySensorAgentPolicyRef = "security-sensor-agent-v1";

    public static SecurityVerdictMapping MapSecurityVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => new SecurityVerdictMapping(Assessment.Pass, EvidenceStatus.Available, null, null),
        "warn" => new SecurityVerdictMapping(Assessment.Concern, null, null, SecuritySensorAgentPolicyRef),
        "block" => new SecurityVerdictMapping(Assessment.Fail, null, Decision.Block, SecuritySensorAgentPolicyRef),
        "unavailable" => new SecurityVerdictMapping(Assessment.Inconclusive, EvidenceStatus.Unavailable, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy security verdict."),
    };

    public static Assessment MapFlowVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => Assessment.Pass,
        "fail" => Assessment.Fail,
        "undetermined" => Assessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy flow verdict."),
    };

    public static Assessment MapAttackVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => Assessment.Pass,
        "finding" => Assessment.Fail,
        "not-applicable" => Assessment.NotApplicable,
        "not-yet-checked" => Assessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy attack verdict."),
    };

    public static QualityChange MapChangeSummary(string legacySummary) => legacySummary switch
    {
        "no-quality-delta" => QualityChange.NoObservedDelta,
        "improved" => QualityChange.Improved,
        "neutral" => QualityChange.Unchanged,
        "regression" => QualityChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(legacySummary), legacySummary, "Unknown legacy change summary."),
    };

    public static Assessment MapChangeAspect(string legacyAspect) => legacyAspect switch
    {
        "good" => Assessment.Pass,
        "mixed" => Assessment.Concern,
        "concerning" => Assessment.Fail,
        "unknown" => Assessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyAspect), legacyAspect, "Unknown legacy change aspect."),
    };

    public static FindingLifecycleMapping MapFindingState(string legacyState) => legacyState switch
    {
        "accepted" => new FindingLifecycleMapping("accepted-risk", "accepted"),
        "accepted-risk" => new FindingLifecycleMapping("accepted-risk", null),
        "falsePositive" => new FindingLifecycleMapping("false-positive", "falsePositive"),
        "false-positive" => new FindingLifecycleMapping("false-positive", null),
        "waived" => new FindingLifecycleMapping("waived", null),
        "resolved" => new FindingLifecycleMapping("resolved", null),
        "open" => new FindingLifecycleMapping("open", null),
        _ => throw new ArgumentOutOfRangeException(nameof(legacyState), legacyState, "Unknown legacy finding state."),
    };

    /// <summary>
    /// Evidence string row of section 8: valid JSON becomes typed tool-result evidence with a
    /// digest and the preserved raw payload; anything else becomes typed evidence with the
    /// preserved original text. Neither branch discards the source string.
    /// </summary>
    public static ObservationEvidenceItem MapEvidenceString(string evidenceId, string legacyEvidence)
    {
        ArgumentNullException.ThrowIfNull(evidenceId);
        ArgumentNullException.ThrowIfNull(legacyEvidence);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyEvidence)));

        if (IsValidJson(legacyEvidence))
        {
            return new ObservationEvidenceItem
            {
                Id = evidenceId,
                Kind = EvidenceKind.ToolResult,
                Summary = "Imported legacy JSON evidence.",
                ContentHash = digest,
                MediaType = "application/json",
                LegacyRaw = legacyEvidence,
            };
        }

        return new ObservationEvidenceItem
        {
            Id = evidenceId,
            Kind = EvidenceKind.Document,
            Summary = legacyEvidence.Length <= 200 ? legacyEvidence : legacyEvidence[..200],
            ContentHash = digest,
            LegacyRaw = legacyEvidence,
        };
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
}

public sealed record SecurityVerdictMapping(
    Assessment Assessment,
    EvidenceStatus? EvidenceStatus,
    Decision? Decision,
    string? PolicyRef);

public sealed record FindingLifecycleMapping(string Canonical, string? LegacyAlias);
