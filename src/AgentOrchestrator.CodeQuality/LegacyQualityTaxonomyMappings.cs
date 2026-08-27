using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record LegacyTaxonomyProjection(
    string LegacyContract,
    string LegacyValue,
    QualityAssessment? Assessment = null,
    QualityChange? Change = null,
    QualityDecision? Decision = null,
    QualityEvidenceStatus? EvidenceStatus = null,
    QualityLifecycleState? Lifecycle = null,
    string? PolicyRef = null);

/// <summary>
/// Pure, case-sensitive adapters for the legacy spellings approved in the data-model taxonomy
/// dossier. Unknown values fail explicitly instead of being coerced into a core term.
/// </summary>
public static class LegacyQualityTaxonomyMappings
{
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    public static LegacyTaxonomyProjection MapSecurityVerdict(string value) => value switch
    {
        "pass" => new("security-verdict", value, QualityAssessment.Pass,
            EvidenceStatus: QualityEvidenceStatus.Available,
            PolicyRef: SecurityPolicyRef),
        "warn" => new("security-verdict", value, QualityAssessment.Concern,
            Decision: QualityDecision.Warn, EvidenceStatus: QualityEvidenceStatus.Available,
            PolicyRef: SecurityPolicyRef),
        "block" => new("security-verdict", value, QualityAssessment.Fail,
            Decision: QualityDecision.Block, EvidenceStatus: QualityEvidenceStatus.Available,
            PolicyRef: SecurityPolicyRef),
        "unavailable" => new("security-verdict", value, QualityAssessment.Inconclusive,
            EvidenceStatus: QualityEvidenceStatus.Unavailable, PolicyRef: SecurityPolicyRef),
        _ => Unknown("security verdict", value),
    };

    public static LegacyTaxonomyProjection MapFlowVerdict(string value) => value switch
    {
        "pass" => new("flow-verdict", value, QualityAssessment.Pass),
        "fail" => new("flow-verdict", value, QualityAssessment.Fail),
        "undetermined" => new("flow-verdict", value, QualityAssessment.Inconclusive),
        _ => Unknown("flow verdict", value),
    };

    public static LegacyTaxonomyProjection MapAttackVerdict(string value) => value switch
    {
        "pass" => new("attack-verdict", value, QualityAssessment.Pass),
        "finding" => new("attack-verdict", value, QualityAssessment.Fail),
        "not-applicable" => new("attack-verdict", value, QualityAssessment.NotApplicable),
        "not-yet-checked" => new("attack-verdict", value, QualityAssessment.NotAssessed),
        _ => Unknown("attack verdict", value),
    };

    public static LegacyTaxonomyProjection MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => new("change-summary", value, Change: QualityChange.NoObservedDelta),
        "improved" => new("change-summary", value, Change: QualityChange.Improved),
        "neutral" => new("change-summary", value, Change: QualityChange.Unchanged),
        "regression" => new("change-summary", value, Change: QualityChange.Regressed),
        _ => Unknown("change summary", value),
    };

    public static LegacyTaxonomyProjection MapChangeAspect(string value) => value switch
    {
        "good" => new("change-aspect", value, QualityAssessment.Pass),
        "mixed" => new("change-aspect", value, QualityAssessment.Concern),
        "concerning" => new("change-aspect", value, QualityAssessment.Fail),
        "unknown" => new("change-aspect", value, QualityAssessment.Inconclusive),
        _ => Unknown("change aspect", value),
    };

    public static LegacyTaxonomyProjection MapFindingState(string value) => value switch
    {
        "open" => new("finding-state", value, Lifecycle: QualityLifecycleState.Open),
        "accepted" or "accepted-risk" =>
            new("finding-state", value, Lifecycle: QualityLifecycleState.AcceptedRisk),
        "waived" => new("finding-state", value, Lifecycle: QualityLifecycleState.Waived),
        "falsePositive" or "false-positive" =>
            new("finding-state", value, Lifecycle: QualityLifecycleState.FalsePositive),
        "resolved" => new("finding-state", value, Lifecycle: QualityLifecycleState.Resolved),
        _ => Unknown("finding state", value),
    };

    public static QualityEvidenceReference MapEvidenceString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return new QualityEvidenceReference
            {
                Id = "legacy-evidence-" + digest[7..],
                Kind = "tool-result",
                Summary = "Legacy JSON evidence preserved without producer-specific interpretation.",
                ContentHash = digest,
                MediaType = "application/json",
                RawContent = parsed.RootElement.Clone(),
                OriginalText = value,
            };
        }
        catch (JsonException)
        {
            return new QualityEvidenceReference
            {
                Id = "legacy-evidence-" + digest[7..],
                Kind = "document",
                Summary = "Legacy plain-text evidence preserved without interpretation.",
                ContentHash = digest,
                MediaType = "text/plain",
                OriginalText = value,
            };
        }
    }

    private static LegacyTaxonomyProjection Unknown(string contract, string value) =>
        throw new ArgumentOutOfRangeException(nameof(value), value,
            $"Unknown legacy {contract} value '{value}'.");
}
