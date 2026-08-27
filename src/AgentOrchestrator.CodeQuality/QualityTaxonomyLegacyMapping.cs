using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure adapters from every current verdict and state spelling onto the core v1 axes in
/// the data-model taxonomy dossier, section 8. These are read-time projections: they never
/// rewrite a committed legacy document and never infer a meaning from an absent field.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public static QualitySecurityVerdictMapping MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new QualitySecurityVerdictMapping(
            QualityAssessment.Pass, QualityEvidenceStatus.Available, QualityDecision.Allow, SecuritySensorPolicyRef),
        SecurityVerdict.Warn => new QualitySecurityVerdictMapping(
            QualityAssessment.Concern, QualityEvidenceStatus.Available, null, SecuritySensorPolicyRef),
        SecurityVerdict.Block => new QualitySecurityVerdictMapping(
            QualityAssessment.Fail, QualityEvidenceStatus.Available, QualityDecision.Block, SecuritySensorPolicyRef),
        SecurityVerdict.Unavailable => new QualitySecurityVerdictMapping(
            QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null, SecuritySensorPolicyRef),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped legacy security verdict."),
    };

    public static QualityAssessment MapFlowReviewVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => QualityAssessment.Pass,
        FlowReviewVerdict.Fail => QualityAssessment.Fail,
        FlowReviewVerdict.Undetermined => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped legacy flow verdict."),
    };

    public static QualityAssessment MapAttackCoverageVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => QualityAssessment.Pass,
        AttackCoverageVerdict.Finding => QualityAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => QualityAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => QualityAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped legacy attack coverage verdict."),
    };

    public static QualityChange MapChangeReviewVerdict(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => QualityChange.NoObservedDelta,
        ChangeReviewVerdict.Improved => QualityChange.Improved,
        ChangeReviewVerdict.Neutral => QualityChange.Unchanged,
        ChangeReviewVerdict.Regression => QualityChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped legacy change summary verdict."),
    };

    /// <summary>
    /// Maps a <see cref="ChangeJudgementAspect.Verdict"/> spelling. Includes the production
    /// "not-reviewed" default from <see cref="ChangeJudgement.NotRun"/> alongside the four
    /// spellings recorded in the dossier's compatibility mapping table.
    /// </summary>
    public static QualityAssessment MapChangeAspectVerdict(string verdict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verdict);
        return verdict switch
        {
            "good" => QualityAssessment.Pass,
            "mixed" => QualityAssessment.Concern,
            "concerning" => QualityAssessment.Fail,
            "unknown" => QualityAssessment.Inconclusive,
            "not-reviewed" => QualityAssessment.NotAssessed,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped legacy change aspect verdict."),
        };
    }

    public static QualityLifecycle MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => QualityLifecycle.Open,
        FindingState.Accepted => QualityLifecycle.AcceptedRisk,
        FindingState.Waived => QualityLifecycle.Waived,
        FindingState.FalsePositive => QualityLifecycle.FalsePositive,
        FindingState.Resolved => QualityLifecycle.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped legacy finding state."),
    };

    /// <summary>
    /// Resolves a raw legacy lifecycle spelling, including the drifted "falsePositive" camel
    /// form the dossier records in F8, onto the canonical lifecycle term. Returns null rather
    /// than guessing when the spelling is not one of the known legacy aliases.
    /// </summary>
    public static QualityLifecycle? TryParseLegacyLifecycleSpelling(string spelling) => spelling switch
    {
        "open" => QualityLifecycle.Open,
        "accepted" or "accepted-risk" => QualityLifecycle.AcceptedRisk,
        "waived" => QualityLifecycle.Waived,
        "false-positive" or "falsePositive" => QualityLifecycle.FalsePositive,
        "resolved" => QualityLifecycle.Resolved,
        _ => null,
    };

    /// <summary>
    /// Classifies a legacy free-text evidence string (used by <c>ReviewFinding.Evidence</c> and
    /// the security JSON-in-a-string convention) as typed evidence without interpreting producer
    /// semantics. Malformed or plain text is preserved verbatim, never discarded.
    /// </summary>
    public static QualityLegacyEvidenceMapping MapLegacyEvidenceString(string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        return IsValidJson(evidence)
            ? new QualityLegacyEvidenceMapping(QualityEvidenceKind.ToolResult, "application/json", evidence, contentHash)
            : new QualityLegacyEvidenceMapping(QualityEvidenceKind.Document, null, evidence, contentHash);
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

public sealed record QualitySecurityVerdictMapping(
    QualityAssessment Assessment,
    QualityEvidenceStatus EvidenceStatus,
    QualityDecision? Decision,
    string? PolicyRef);

/// <summary>An evidence string re-typed without discarding or reinterpreting its original bytes.</summary>
public sealed record QualityLegacyEvidenceMapping(
    QualityEvidenceKind Kind,
    string? MediaType,
    string PreservedContent,
    string ContentHash);
