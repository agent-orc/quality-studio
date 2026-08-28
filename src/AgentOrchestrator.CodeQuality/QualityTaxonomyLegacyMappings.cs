using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure, deterministic projections from every current verdict/state spelling (dossier section 8,
/// "Compatibility mappings") onto the core v1 taxonomy axes. These are adapters only: they read
/// legacy values and return core terms without touching any writer or persisted file.
/// </summary>
public static class QualityTaxonomyLegacyMappings
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public static QualityAssessment FromSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => QualityAssessment.Pass,
        SecurityVerdict.Warn => QualityAssessment.Concern,
        SecurityVerdict.Block => QualityAssessment.Fail,
        SecurityVerdict.Unavailable => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped security verdict."),
    };

    public static QualityEvidenceStatus EvidenceStatusFromSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => QualityEvidenceStatus.Available,
        SecurityVerdict.Warn => QualityEvidenceStatus.Available,
        SecurityVerdict.Block => QualityEvidenceStatus.Available,
        SecurityVerdict.Unavailable => QualityEvidenceStatus.Unavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped security verdict."),
    };

    /// <summary>Non-null only for the verdicts the dossier assigns a retained policy disposition.</summary>
    public static QualityDecision? DecisionFromSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Block => QualityDecision.Block,
        _ => null,
    };

    /// <summary>Non-null only where the dossier says the legacy policy result is retained separately.</summary>
    public static string? PolicyRefFromSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Warn => SecuritySensorPolicyRef,
        SecurityVerdict.Unavailable => SecuritySensorPolicyRef,
        _ => null,
    };

    public static QualityAssessment FromFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => QualityAssessment.Pass,
        FlowReviewVerdict.Fail => QualityAssessment.Fail,
        FlowReviewVerdict.Undetermined => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped flow verdict."),
    };

    public static QualityAssessment FromAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => QualityAssessment.Pass,
        AttackCoverageVerdict.Finding => QualityAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => QualityAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => QualityAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped attack verdict."),
    };

    public static QualityChange FromChangeSummary(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => QualityChange.NoObservedDelta,
        ChangeReviewVerdict.Improved => QualityChange.Improved,
        ChangeReviewVerdict.Neutral => QualityChange.Unchanged,
        ChangeReviewVerdict.Regression => QualityChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped change verdict."),
    };

    /// <summary>
    /// Change aspect verdicts (<c>ChangeJudgementAspect.Verdict</c>) are free-form strings in the
    /// legacy schema. The dossier's four documented spellings map exactly; anything else is
    /// reported explicitly rather than guessed at.
    /// </summary>
    public static QualityAssessment FromChangeAspectVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "good" => QualityAssessment.Pass,
        "mixed" => QualityAssessment.Concern,
        "concerning" => QualityAssessment.Fail,
        "unknown" => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(legacyVerdict), legacyVerdict, "Unmapped change aspect verdict."),
    };

    public static QualityLifecycleState FromFindingState(FindingState state) => state switch
    {
        FindingState.Open => QualityLifecycleState.Open,
        FindingState.Accepted => QualityLifecycleState.AcceptedRisk,
        FindingState.Waived => QualityLifecycleState.Waived,
        FindingState.FalsePositive => QualityLifecycleState.FalsePositive,
        FindingState.Resolved => QualityLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped finding state."),
    };

    /// <summary>
    /// Accepts every spelling actually written to disk, including flow-review's camelCase
    /// <c>falsePositive</c> (dossier F8: canonical spelling already drifts).
    /// </summary>
    public static QualityLifecycleState FromFindingStateSpelling(string legacyValue) => legacyValue switch
    {
        "open" => QualityLifecycleState.Open,
        "accepted" => QualityLifecycleState.AcceptedRisk,
        "waived" => QualityLifecycleState.Waived,
        "false-positive" or "falsePositive" => QualityLifecycleState.FalsePositive,
        "resolved" => QualityLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(
            nameof(legacyValue), legacyValue, "Unmapped finding state spelling."),
    };

    /// <summary>
    /// Converts a legacy opportunistic-string evidence payload (<c>ReviewFinding.Evidence</c>,
    /// <c>SecurityReviewEvidence</c>) into a typed evidence item. Valid JSON becomes a
    /// <c>tool-result</c>; anything else is preserved as a <c>document</c> with the original text.
    /// The raw payload is always preserved, never discarded (dossier section 8 footnote).
    /// </summary>
    public static QualityObservationEvidence FromEvidenceString(string id, string rawEvidence)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawEvidence);
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawEvidence)));

        if (IsWellFormedJson(rawEvidence))
        {
            return new QualityObservationEvidence(
                id,
                QualityEvidenceKind.ToolResult,
                "Imported machine-readable evidence.",
                ContentHash: contentHash);
        }

        return new QualityObservationEvidence(
            id,
            QualityEvidenceKind.Document,
            rawEvidence.Length <= 240 ? rawEvidence : rawEvidence[..240],
            ContentHash: contentHash);
    }

    private static bool IsWellFormedJson(string value)
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
