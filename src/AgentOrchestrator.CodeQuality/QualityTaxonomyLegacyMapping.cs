using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Projects a legacy security combination verdict onto the core v1 <c>assessment</c> axis
/// (dossier section 8, "Security verdict" rows). <c>PolicyRef</c> retains the legacy policy that
/// produced <paramref name="Decision"/> so a policy disposition is never stored as an evidence fact.</summary>
public sealed record SecurityVerdictProjection(
    ObservationAssessment Assessment,
    ObservationEvidenceStatus EvidenceStatus,
    string? Decision,
    string? PolicyRef);

/// <summary>Pure, deterministic projections from every legacy Quality Studio verdict and lifecycle
/// spelling onto the <c>quality-studio/core@1.0.0</c> canonical axes (dossier section 8). These are
/// adapters only: no writer is changed to call them in T1, and no historical file is rewritten.</summary>
public static class QualityTaxonomyLegacyMapping
{
    public const string SecuritySensorPolicyRef = "security-sensor-agent-v1";

    /// <summary>Maps a legacy <see cref="SecurityVerdict"/> ("pass"/"warn"/"block"/"unavailable").</summary>
    public static SecurityVerdictProjection MapSecurityVerdict(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new SecurityVerdictProjection(
            ObservationAssessment.Pass, ObservationEvidenceStatus.Available, "allow", null),
        SecurityVerdict.Warn => new SecurityVerdictProjection(
            ObservationAssessment.Concern, ObservationEvidenceStatus.Available, null, SecuritySensorPolicyRef),
        SecurityVerdict.Block => new SecurityVerdictProjection(
            ObservationAssessment.Fail, ObservationEvidenceStatus.Available, "block", SecuritySensorPolicyRef),
        SecurityVerdict.Unavailable => new SecurityVerdictProjection(
            ObservationAssessment.Inconclusive, ObservationEvidenceStatus.Unavailable, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported legacy security verdict."),
    };

    /// <summary>Maps a legacy <see cref="FlowReviewVerdict"/> ("pass"/"fail"/"undetermined").</summary>
    public static ObservationAssessment MapFlowVerdict(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => ObservationAssessment.Pass,
        FlowReviewVerdict.Fail => ObservationAssessment.Fail,
        FlowReviewVerdict.Undetermined => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported legacy flow verdict."),
    };

    /// <summary>Maps a legacy <see cref="AttackCoverageVerdict"/>
    /// ("pass"/"finding"/"not-applicable"/"not-yet-checked").</summary>
    public static ObservationAssessment MapAttackVerdict(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => ObservationAssessment.Pass,
        AttackCoverageVerdict.Finding => ObservationAssessment.Fail,
        AttackCoverageVerdict.NotApplicable => ObservationAssessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => ObservationAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported legacy attack verdict."),
    };

    /// <summary>Maps a legacy change-review summary <see cref="ChangeReviewVerdict"/> onto the
    /// <c>change</c> axis ("no-quality-delta"/"improved"/"neutral"/"regression").</summary>
    public static string MapChangeSummary(ChangeReviewVerdict summary) => summary switch
    {
        ChangeReviewVerdict.NoQualityDelta => "no-observed-delta",
        ChangeReviewVerdict.Improved => "improved",
        ChangeReviewVerdict.Neutral => "unchanged",
        ChangeReviewVerdict.Regression => "regressed",
        _ => throw new ArgumentOutOfRangeException(nameof(summary), summary, "Unsupported legacy change summary."),
    };

    /// <summary>Maps a legacy change-aspect verdict string ("good"/"mixed"/"concerning"/"unknown")
    /// onto the <c>assessment</c> axis.</summary>
    public static ObservationAssessment MapChangeAspectVerdict(string verdict) => verdict switch
    {
        "good" => ObservationAssessment.Pass,
        "mixed" => ObservationAssessment.Concern,
        "concerning" => ObservationAssessment.Fail,
        "unknown" => ObservationAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unsupported legacy change aspect verdict."),
    };

    /// <summary>Maps a legacy <see cref="FindingState"/> onto the <c>lifecycle</c> axis. <c>Accepted</c>
    /// becomes the explicit alias <c>accepted-risk</c>; both legacy spellings of false positive
    /// ("false-positive" from finding state, "falsePositive" from flow-review v1) land on the same term.</summary>
    public static string MapFindingState(FindingState state) => state switch
    {
        FindingState.Open => "open",
        FindingState.Accepted => "accepted-risk",
        FindingState.Waived => "waived",
        FindingState.FalsePositive => "false-positive",
        FindingState.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported legacy finding state."),
    };

    /// <summary>Maps the legacy flow-review v1 lifecycle spelling ("open"/"falsePositive") onto the
    /// same <c>lifecycle</c> axis as <see cref="MapFindingState"/>.</summary>
    public static string MapFlowLifecycleState(string state) => state switch
    {
        "open" => "open",
        "falsePositive" => "false-positive",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported legacy flow lifecycle state."),
    };

    /// <summary>Projects a legacy free-text <c>evidence</c> string (dossier section 8, "Evidence
    /// string" rows) onto a typed <see cref="QualityObservationEvidence"/> item. Valid JSON payloads
    /// become <c>tool-result</c> evidence with the parsed payload preserved under <c>payload</c>;
    /// anything else becomes <c>document</c> evidence with the original text preserved verbatim.</summary>
    public static QualityObservationEvidence MapEvidenceString(string id, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(evidence);
        var digest = ContentHash(evidence);
        if (TryParseJson(evidence, out var payload))
        {
            var locator = new Dictionary<string, JsonElement>
            {
                ["contentType"] = JsonSerializer.SerializeToElement("application/json"),
                ["payload"] = payload,
            };
            return new QualityObservationEvidence(
                id, ObservationEvidenceKind.ToolResult, locator,
                "Legacy evidence carried a JSON payload.", digest);
        }

        var textLocator = new Dictionary<string, JsonElement>
        {
            ["contentType"] = JsonSerializer.SerializeToElement("text/plain"),
            ["text"] = JsonSerializer.SerializeToElement(evidence),
        };
        return new QualityObservationEvidence(
            id, ObservationEvidenceKind.Document, textLocator,
            "Legacy evidence carried free text.", digest);
    }

    private static bool TryParseJson(string evidence, out JsonElement payload)
    {
        try
        {
            using var document = JsonDocument.Parse(evidence);
            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            payload = default;
            return false;
        }
    }

    private static string ContentHash(string content) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
