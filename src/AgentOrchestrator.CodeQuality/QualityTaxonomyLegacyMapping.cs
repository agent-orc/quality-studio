using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Pure adapters from every current contract's verdict, state, and evidence spelling to the
/// quality-studio/core v1 axes (dossier section 8, "Compatibility mappings"). These are
/// projections, not rewrites: nothing here reads or writes a file, and no caller is required
/// to switch its own storage yet.
/// </summary>
public static class QualityTaxonomyLegacyMapping
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    public sealed record SecurityVerdictProjection(
        ObservationAssessment Assessment,
        ObservationEvidenceStatus EvidenceStatus,
        ObservationDecision? Decision,
        string? PolicyRef);

    public static SecurityVerdictProjection MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new(ObservationAssessment.Pass, ObservationEvidenceStatus.Available, ObservationDecision.Allow, null),
        SecurityVerdict.Warn => new(ObservationAssessment.Concern, ObservationEvidenceStatus.Available, null, SecuritySensorPolicyRef),
        SecurityVerdict.Block => new(ObservationAssessment.Fail, ObservationEvidenceStatus.Available, ObservationDecision.Block, SecuritySensorPolicyRef),
        SecurityVerdict.Unavailable => new(ObservationAssessment.Inconclusive, ObservationEvidenceStatus.Unavailable, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown security verdict."),
    };

    public static ObservationAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => ObservationAssessment.Pass,
        FlowReviewVerdict.Fail => ObservationAssessment.Fail,
        FlowReviewVerdict.Undetermined => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown flow-review verdict."),
    };

    public static ObservationAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => ObservationAssessment.Pass,
        AttackCoverageVerdict.Finding => ObservationAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => ObservationAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => ObservationAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown attack-coverage verdict."),
    };

    /// <summary>Maps <c>change-review.v1</c> <c>summary</c> onto the <c>change</c> axis.</summary>
    public static ObservationChange MapChangeSummary(string legacySummary) => legacySummary switch
    {
        "no-quality-delta" => ObservationChange.NoObservedDelta,
        "improved" => ObservationChange.Improved,
        "neutral" => ObservationChange.Unchanged,
        "regression" => ObservationChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(legacySummary), legacySummary, "Unknown change-review summary."),
    };

    /// <summary>Maps a <c>change-review.v1</c> aspect verdict onto the <c>assessment</c> axis.</summary>
    public static ObservationAssessment MapChangeAspectVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "good" => ObservationAssessment.Pass,
        "mixed" => ObservationAssessment.Concern,
        "concerning" => ObservationAssessment.Fail,
        "unknown" => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown change-aspect verdict."),
    };

    /// <summary>
    /// Maps a finding-state spelling onto the <c>lifecycle</c> axis, including the
    /// <c>accepted</c> read alias and both the camelCase and kebab-case false-positive spellings
    /// that already drift between the finding-state projection and flow-review v1 (dossier F8).
    /// </summary>
    public static ObservationLifecycle MapFindingState(string legacyState) => legacyState switch
    {
        "open" => ObservationLifecycle.Open,
        "accepted" or "accepted-risk" => ObservationLifecycle.AcceptedRisk,
        "waived" => ObservationLifecycle.Waived,
        "false-positive" or "falsePositive" => ObservationLifecycle.FalsePositive,
        "resolved" => ObservationLifecycle.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyState), legacyState, "Unknown finding lifecycle state."),
    };

    public sealed record MappedEvidence(ObservationEvidenceKind Kind, string Summary, string RawPayload, string ContentHash);

    /// <summary>
    /// A general <see cref="ReviewFinding"/> stores evidence as an opaque string; security
    /// wraps machine JSON in it (dossier F4). Valid JSON becomes typed tool-result evidence
    /// with the raw payload preserved; anything else becomes typed document evidence with the
    /// original text preserved unmodified.
    /// </summary>
    public static MappedEvidence MapEvidenceString(string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        try
        {
            using var _ = JsonDocument.Parse(evidence);
            return new MappedEvidence(ObservationEvidenceKind.ToolResult, "Imported machine-sensor evidence.", evidence, contentHash);
        }
        catch (JsonException)
        {
            var summary = evidence.Length <= 300 ? evidence : evidence[..300];
            return new MappedEvidence(ObservationEvidenceKind.Document, summary, evidence, contentHash);
        }
    }
}
