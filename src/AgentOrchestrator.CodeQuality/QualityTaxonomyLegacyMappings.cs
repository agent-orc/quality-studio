using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure adapters from every current verdict and lifecycle spelling onto the quality-studio/core
/// v1 axes. These are read-time projections only: see docs/operations/data-model-taxonomy/index.html
/// section 8. Nothing here writes to or changes a legacy contract.
/// </summary>
public static class QualityTaxonomyLegacyMappings
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public sealed record SecurityVerdictMapping(
        QualityAssessment Assessment,
        QualityEvidenceStatus EvidenceStatus,
        QualityDecision? Decision,
        string? PolicyRef);

    /// <summary>Security verdict -&gt; assessment plus an optional policy disposition (section 8, rows 1-4).</summary>
    public static SecurityVerdictMapping MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new(QualityAssessment.Pass, QualityEvidenceStatus.Available,
            QualityDecision.Allow, SecuritySensorPolicyRef),
        SecurityVerdict.Warn => new(QualityAssessment.Concern, QualityEvidenceStatus.Available,
            null, SecuritySensorPolicyRef),
        SecurityVerdict.Block => new(QualityAssessment.Fail, QualityEvidenceStatus.Available,
            QualityDecision.Block, SecuritySensorPolicyRef),
        SecurityVerdict.Unavailable => new(QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable,
            null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported security verdict."),
    };

    /// <summary>Flow verdict -&gt; assessment (section 8, row 5).</summary>
    public static QualityAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => QualityAssessment.Pass,
        FlowReviewVerdict.Fail => QualityAssessment.Fail,
        FlowReviewVerdict.Undetermined => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported flow verdict."),
    };

    /// <summary>Attack coverage verdict -&gt; assessment (section 8, row 6).</summary>
    public static QualityAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => QualityAssessment.Pass,
        AttackCoverageVerdict.Finding => QualityAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => QualityAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => QualityAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported attack verdict."),
    };

    /// <summary>Change summary verdict -&gt; change axis (section 8, row 7).</summary>
    public static QualityChange MapChangeSummary(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => QualityChange.NoObservedDelta,
        ChangeReviewVerdict.Improved => QualityChange.Improved,
        ChangeReviewVerdict.Neutral => QualityChange.Unchanged,
        ChangeReviewVerdict.Regression => QualityChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported change summary verdict."),
    };

    /// <summary>Change aspect verdict -&gt; assessment (section 8, row 8).</summary>
    public static QualityAssessment MapChangeAspectVerdict(string verdict) => verdict switch
    {
        "good" => QualityAssessment.Pass,
        "mixed" => QualityAssessment.Concern,
        "concerning" => QualityAssessment.Fail,
        "unknown" => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported change aspect verdict."),
    };

    /// <summary>Finding state -&gt; lifecycle axis (section 8, rows 9-10).</summary>
    public static QualityLifecycleState MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => QualityLifecycleState.Open,
        FindingState.Accepted => QualityLifecycleState.AcceptedRisk,
        FindingState.Waived => QualityLifecycleState.Waived,
        FindingState.FalsePositive => QualityLifecycleState.FalsePositive,
        FindingState.Resolved => QualityLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported finding state."),
    };

    /// <summary>
    /// Normalizes the two committed spellings for the same state (F8): finding-state's
    /// <c>false-positive</c> and flow-review's <c>falsePositive</c>, plus the <c>accepted</c> alias.
    /// </summary>
    public static QualityLifecycleState MapLifecycleSpelling(string spelling) => spelling switch
    {
        "open" => QualityLifecycleState.Open,
        "accepted" or "accepted-risk" => QualityLifecycleState.AcceptedRisk,
        "waived" => QualityLifecycleState.Waived,
        "false-positive" or "falsePositive" => QualityLifecycleState.FalsePositive,
        "resolved" => QualityLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, "Unsupported lifecycle spelling."),
    };

    public sealed record LegacyEvidenceMapping(
        QualityEvidenceKind Kind,
        string Summary,
        string ContentHash,
        string PreservedText,
        bool ParsedAsJson);

    /// <summary>
    /// Legacy evidence string -&gt; typed evidence (section 8, rows 11-12). Valid JSON becomes
    /// tool-result evidence; anything else becomes document evidence. The original text is always
    /// preserved and hashed, never discarded.
    /// </summary>
    public static LegacyEvidenceMapping? MapLegacyEvidenceString(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return null;

        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        if (IsValidJson(evidence))
            return new LegacyEvidenceMapping(QualityEvidenceKind.ToolResult, "Imported machine evidence.", contentHash, evidence, true);

        var summary = evidence.Length > 200 ? evidence[..200] : evidence;
        return new LegacyEvidenceMapping(QualityEvidenceKind.Document, summary, contentHash, evidence, false);
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
