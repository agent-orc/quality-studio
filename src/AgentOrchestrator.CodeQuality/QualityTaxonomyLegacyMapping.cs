using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Deterministic adapters from every legacy verdict/state spelling to the core v1 axes defined
/// in docs/operations/data-model-taxonomy/index.html section 8. Pure functions only: they never
/// read or write a file. Migrating a stored document (T5) is a separate concern from computing
/// what its values mean under the new taxonomy.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public static QualityObservationAssessment MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => QualityObservationAssessment.Pass,
        SecurityVerdict.Warn => QualityObservationAssessment.Concern,
        SecurityVerdict.Block => QualityObservationAssessment.Fail,
        SecurityVerdict.Unavailable => QualityObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown security verdict."),
    };

    public static QualityObservationEvidenceStatus MapSecurityEvidenceStatus(SecurityVerdict verdict) =>
        verdict == SecurityVerdict.Unavailable
            ? QualityObservationEvidenceStatus.Unavailable
            : QualityObservationEvidenceStatus.Available;

    /// <summary>
    /// The retained security-sensor-agent-v1 policy decision. Pass and block map to an explicit
    /// decision; warn and unavailable keep their disposition in the legacy policy result instead
    /// of forcing one, per the dossier's "no policy disposition stored as an evidence assessment"
    /// invariant.
    /// </summary>
    public static string? MapSecurityDecision(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => "allow",
        SecurityVerdict.Block => "block",
        _ => null,
    };

    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public static QualityObservationAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => QualityObservationAssessment.Pass,
        FlowReviewVerdict.Fail => QualityObservationAssessment.Fail,
        FlowReviewVerdict.Undetermined => QualityObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown flow verdict."),
    };

    public static QualityObservationAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => QualityObservationAssessment.Pass,
        AttackCoverageVerdict.Finding => QualityObservationAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => QualityObservationAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => QualityObservationAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown attack verdict."),
    };

    /// <summary>Maps a change-review.v1 summary verdict onto the "change" axis.</summary>
    public static string MapChangeSummary(string legacySummary) => legacySummary switch
    {
        "no-quality-delta" => "no-observed-delta",
        "improved" => "improved",
        "neutral" => "unchanged",
        "regression" => "regressed",
        _ => throw new ArgumentOutOfRangeException(nameof(legacySummary), legacySummary, "Unknown change summary verdict."),
    };

    /// <summary>Maps a change-review.v1 aspect verdict onto the "assessment" axis.</summary>
    public static QualityObservationAssessment MapChangeAspectVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "good" => QualityObservationAssessment.Pass,
        "mixed" => QualityObservationAssessment.Concern,
        "concerning" => QualityObservationAssessment.Fail,
        "unknown" => QualityObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown change aspect verdict."),
    };

    /// <summary>Maps finding-state.v1 to the "lifecycle" axis. Legacy "accepted" is a read alias for "accepted-risk".</summary>
    public static string MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => "open",
        FindingState.Accepted => "accepted-risk",
        FindingState.Waived => "waived",
        FindingState.FalsePositive => "false-positive",
        FindingState.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown finding state."),
    };

    public sealed record LegacyEvidenceMapping(
        QualityObservationEvidenceKind Kind,
        string? MediaType,
        string Summary,
        string PreservedContent,
        string ContentHash);

    /// <summary>
    /// Classifies a legacy free-text/JSON evidence string (ReviewFinding.Evidence) into a typed
    /// evidence item without interpreting producer-specific semantics, per section 8/11.
    /// </summary>
    public static LegacyEvidenceMapping MapEvidenceString(string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        return IsValidJson(evidence)
            ? new LegacyEvidenceMapping(
                QualityObservationEvidenceKind.ToolResult, "application/json", "Imported machine evidence.", evidence, hash)
            : new LegacyEvidenceMapping(
                QualityObservationEvidenceKind.Document, null, "Imported evidence text.", evidence, hash);
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
