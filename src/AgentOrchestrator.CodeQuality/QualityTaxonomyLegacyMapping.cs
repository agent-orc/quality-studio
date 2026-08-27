namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure, one-way projections from every current verdict, state, and evidence shape onto the
/// core v1 taxonomy axes in dossier section 8. These are adapters for building or reading
/// observations; they never rewrite the legacy documents they read from.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public sealed record SecurityVerdictMapping(
        QualityAssessment Assessment,
        QualityEvidenceStatus? EvidenceStatus,
        string? Decision,
        string PolicyRef);

    public static SecurityVerdictMapping MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new(QualityAssessment.Pass, QualityEvidenceStatus.Available, "allow", SecuritySensorPolicyRef),
        SecurityVerdict.Warn => new(QualityAssessment.Concern, null, null, SecuritySensorPolicyRef),
        SecurityVerdict.Block => new(QualityAssessment.Fail, null, "block", SecuritySensorPolicyRef),
        SecurityVerdict.Unavailable => new(QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null, SecuritySensorPolicyRef),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped security verdict."),
    };

    public static QualityAssessment MapFlowReviewVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => QualityAssessment.Pass,
        FlowReviewVerdict.Fail => QualityAssessment.Fail,
        FlowReviewVerdict.Undetermined => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped flow review verdict."),
    };

    public static QualityAssessment MapAttackCoverageVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => QualityAssessment.Pass,
        AttackCoverageVerdict.Finding => QualityAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => QualityAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => QualityAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped attack coverage verdict."),
    };

    public static string MapChangeReviewVerdict(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => "no-observed-delta",
        ChangeReviewVerdict.Improved => "improved",
        ChangeReviewVerdict.Neutral => "unchanged",
        ChangeReviewVerdict.Regression => "regressed",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unmapped change review verdict."),
    };

    /// <summary>Maps a change-judgement aspect verdict string (<c>good/mixed/concerning/unknown</c>).</summary>
    public static QualityAssessment MapChangeAspectVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "good" => QualityAssessment.Pass,
        "mixed" => QualityAssessment.Concern,
        "concerning" => QualityAssessment.Fail,
        "unknown" => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unmapped change-aspect verdict."),
    };

    public static string MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => "open",
        FindingState.Accepted => "accepted-risk",
        FindingState.Waived => "waived",
        FindingState.FalsePositive => "false-positive",
        FindingState.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unmapped finding state."),
    };

    /// <summary>Normalizes a spelling drift (<c>accepted</c>, <c>falsePositive</c>) to its canonical lifecycle id.</summary>
    public static string NormalizeLifecycleAlias(string legacyValue) => legacyValue switch
    {
        "accepted" => "accepted-risk",
        "falsePositive" => "false-positive",
        "false-positive" => "false-positive",
        _ => legacyValue,
    };

    /// <summary>
    /// Classifies a legacy free-form evidence string. Valid JSON becomes a <c>tool-result</c>
    /// with the parsed payload preserved as the summary source; anything else becomes a
    /// <c>document</c> item preserving the original text unchanged.
    /// </summary>
    public static QualityEvidenceKind ClassifyLegacyEvidenceString(string evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(evidence);
            return QualityEvidenceKind.ToolResult;
        }
        catch (System.Text.Json.JsonException)
        {
            return QualityEvidenceKind.Document;
        }
    }
}
