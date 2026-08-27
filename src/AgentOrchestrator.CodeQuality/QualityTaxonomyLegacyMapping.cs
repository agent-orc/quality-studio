using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Result of classifying a legacy free-text/JSON <c>evidence</c> string into a typed
/// evidence kind. Adapters convert this into an <see cref="ObservationEvidence"/> item;
/// nothing here loses the original content.
/// </summary>
public sealed record LegacyEvidenceClassification(
    ObservationEvidenceKind Kind,
    bool IsJson,
    string PreservedContent);

/// <summary>
/// Deterministic, side-effect-free adapters from every legacy verdict/state spelling in
/// the repository to the canonical <c>quality-studio/core@1.0.0</c> axes described in the
/// dossier's compatibility mapping table. These are pure lookups: they never mutate
/// legacy files and never invent a value for input they do not recognize.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public static (ObservationAssessment Assessment, EvidenceStatus EvidenceStatus, PolicyDecision? Decision)
        MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
        {
            SecurityVerdict.Pass => (ObservationAssessment.Pass, EvidenceStatus.Available, PolicyDecision.Allow),
            SecurityVerdict.Warn => (ObservationAssessment.Concern, EvidenceStatus.Available, null),
            SecurityVerdict.Block => (ObservationAssessment.Fail, EvidenceStatus.Available, PolicyDecision.Block),
            SecurityVerdict.Unavailable => (ObservationAssessment.Inconclusive, EvidenceStatus.Unavailable, null),
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unrecognized security verdict."),
        };

    public static ObservationAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => ObservationAssessment.Pass,
        FlowReviewVerdict.Fail => ObservationAssessment.Fail,
        FlowReviewVerdict.Undetermined => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unrecognized flow verdict."),
    };

    public static ObservationAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => ObservationAssessment.Pass,
        AttackCoverageVerdict.Finding => ObservationAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => ObservationAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => ObservationAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unrecognized attack coverage verdict."),
    };

    public static ObservationChange MapChangeSummary(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => ObservationChange.NoObservedDelta,
        ChangeReviewVerdict.Improved => ObservationChange.Improved,
        ChangeReviewVerdict.Neutral => ObservationChange.Unchanged,
        ChangeReviewVerdict.Regression => ObservationChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unrecognized change summary verdict."),
    };

    /// <summary>
    /// Change-aspect verdicts (<c>good/mixed/concerning/unknown</c>) are stored as free
    /// strings today (<see cref="ChangeJudgementAspect.Verdict"/>); there is no enum to
    /// switch over, so this maps the exact legacy spelling.
    /// </summary>
    public static ObservationAssessment MapChangeAspectVerdict(string verdict) => verdict switch
    {
        "good" => ObservationAssessment.Pass,
        "mixed" => ObservationAssessment.Concern,
        "concerning" => ObservationAssessment.Fail,
        "unknown" => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unrecognized change aspect verdict."),
    };

    public static ObservationLifecycleState MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => ObservationLifecycleState.Open,
        FindingState.Accepted => ObservationLifecycleState.AcceptedRisk,
        FindingState.Waived => ObservationLifecycleState.Waived,
        FindingState.FalsePositive => ObservationLifecycleState.FalsePositive,
        FindingState.Resolved => ObservationLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unrecognized finding state."),
    };

    /// <summary>
    /// Covers both drifted legacy spellings for the same state
    /// (<c>false-positive</c> from finding-state v1, <c>falsePositive</c> from
    /// flow-review v1) and the <c>accepted</c> to <c>accepted-risk</c> rename.
    /// </summary>
    public static ObservationLifecycleState MapLifecycleAlias(string legacySpelling) => legacySpelling switch
    {
        "open" => ObservationLifecycleState.Open,
        "accepted" or "accepted-risk" => ObservationLifecycleState.AcceptedRisk,
        "waived" => ObservationLifecycleState.Waived,
        "false-positive" or "falsePositive" => ObservationLifecycleState.FalsePositive,
        "resolved" => ObservationLifecycleState.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(legacySpelling), legacySpelling, "Unrecognized lifecycle spelling."),
    };

    /// <summary>
    /// Classifies a legacy <c>ReviewFinding.Evidence</c> string. Valid JSON becomes a
    /// <c>tool-result</c> item (security wraps machine JSON this way today); anything else
    /// is preserved as a typed item with the original text as its summary. Nothing is
    /// discarded, and malformed JSON is treated as plain text rather than rejected.
    /// </summary>
    public static LegacyEvidenceClassification ClassifyLegacyEvidence(string evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            using var _ = JsonDocument.Parse(evidence);
            return new LegacyEvidenceClassification(ObservationEvidenceKind.ToolResult, true, evidence);
        }
        catch (JsonException)
        {
            return new LegacyEvidenceClassification(ObservationEvidenceKind.Document, false, evidence);
        }
    }
}
