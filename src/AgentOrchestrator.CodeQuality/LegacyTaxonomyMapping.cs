namespace AgentOrchestrator.CodeQuality;

/// <summary>Result of mapping a legacy security verdict onto the core taxonomy axes.</summary>
public sealed record SecurityVerdictMapping(
    TaxonomyAssessment Assessment,
    TaxonomyEvidenceStatus EvidenceStatus,
    TaxonomyDecision? Decision,
    string? PolicyRef);

/// <summary>
/// Pure, deterministic adapters from every legacy verdict and lifecycle spelling to the core
/// taxonomy terms pinned in the data-model taxonomy dossier (section 8). These functions never
/// touch disk and never change a legacy writer; they only translate already-produced values.
/// </summary>
public static class LegacyTaxonomyMapping
{
    public const string SecuritySensorPolicyId = "security-sensor-agent-v1";

    public static SecurityVerdictMapping MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new(TaxonomyAssessment.Pass, TaxonomyEvidenceStatus.Available, TaxonomyDecision.Allow, null),
        SecurityVerdict.Warn => new(TaxonomyAssessment.Concern, TaxonomyEvidenceStatus.Available, null, SecuritySensorPolicyId),
        SecurityVerdict.Block => new(TaxonomyAssessment.Fail, TaxonomyEvidenceStatus.Available, TaxonomyDecision.Block, SecuritySensorPolicyId),
        SecurityVerdict.Unavailable => new(TaxonomyAssessment.Inconclusive, TaxonomyEvidenceStatus.Unavailable, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported security verdict."),
    };

    public static TaxonomyAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => TaxonomyAssessment.Pass,
        FlowReviewVerdict.Fail => TaxonomyAssessment.Fail,
        FlowReviewVerdict.Undetermined => TaxonomyAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported flow review verdict."),
    };

    public static TaxonomyAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => TaxonomyAssessment.Pass,
        AttackCoverageVerdict.Finding => TaxonomyAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => TaxonomyAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => TaxonomyAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported attack coverage verdict."),
    };

    public static TaxonomyChange MapChangeSummary(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => TaxonomyChange.NoObservedDelta,
        ChangeReviewVerdict.Improved => TaxonomyChange.Improved,
        ChangeReviewVerdict.Neutral => TaxonomyChange.Unchanged,
        ChangeReviewVerdict.Regression => TaxonomyChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported change review verdict."),
    };

    /// <summary>Maps a change-aspect verdict string (<c>good/mixed/concerning/unknown</c>).</summary>
    public static TaxonomyAssessment MapChangeAspectVerdict(string verdict) => verdict switch
    {
        "good" => TaxonomyAssessment.Pass,
        "mixed" => TaxonomyAssessment.Concern,
        "concerning" => TaxonomyAssessment.Fail,
        "unknown" => TaxonomyAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported change aspect verdict."),
    };

    public static TaxonomyLifecycle MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => TaxonomyLifecycle.Open,
        FindingState.Accepted => TaxonomyLifecycle.AcceptedRisk,
        FindingState.Waived => TaxonomyLifecycle.Waived,
        FindingState.FalsePositive => TaxonomyLifecycle.FalsePositive,
        FindingState.Resolved => TaxonomyLifecycle.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported finding state."),
    };

    /// <summary>
    /// Maps every legacy lifecycle spelling seen in the repository, including the
    /// <c>false-positive</c>/<c>falsePositive</c> and <c>accepted</c>/<c>accepted-risk</c> drift
    /// documented in the dossier's canonical-spelling finding (F8).
    /// </summary>
    public static TaxonomyLifecycle MapFindingStateSpelling(string spelling) => spelling switch
    {
        "open" => TaxonomyLifecycle.Open,
        "accepted" or "accepted-risk" => TaxonomyLifecycle.AcceptedRisk,
        "waived" => TaxonomyLifecycle.Waived,
        "falsePositive" or "false-positive" => TaxonomyLifecycle.FalsePositive,
        "resolved" => TaxonomyLifecycle.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, "Unsupported finding state spelling."),
    };
}
