using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public enum LegacyTaxonomyContract
{
    SecurityVerdict,
    FlowVerdict,
    AttackVerdict,
    ChangeSummary,
    ChangeAspect,
    FindingState,
}

public sealed record LegacyTaxonomyMappingResult(
    LegacyTaxonomyContract Contract,
    string LegacyValue,
    QualityAssessment? Assessment = null,
    QualityEvidenceStatus? EvidenceStatus = null,
    QualityDecisionValue? Decision = null,
    string? PolicyRef = null,
    string? Change = null,
    string? Lifecycle = null);

public static class LegacyTaxonomyMapper
{
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    public static LegacyTaxonomyMappingResult Map(LegacyTaxonomyContract contract, string legacyValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyValue);
        return (contract, legacyValue) switch
        {
            (LegacyTaxonomyContract.SecurityVerdict, "pass") => new(
                contract, legacyValue, QualityAssessment.Pass, QualityEvidenceStatus.Available,
                QualityDecisionValue.Allow, SecurityPolicyRef),
            (LegacyTaxonomyContract.SecurityVerdict, "warn") => new(
                contract, legacyValue, QualityAssessment.Concern, QualityEvidenceStatus.Available,
                QualityDecisionValue.Warn, SecurityPolicyRef),
            (LegacyTaxonomyContract.SecurityVerdict, "block") => new(
                contract, legacyValue, QualityAssessment.Fail, QualityEvidenceStatus.Available,
                QualityDecisionValue.Block, SecurityPolicyRef),
            (LegacyTaxonomyContract.SecurityVerdict, "unavailable") => new(
                contract, legacyValue, QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable,
                PolicyRef: SecurityPolicyRef),

            (LegacyTaxonomyContract.FlowVerdict, "pass") => new(
                contract, legacyValue, QualityAssessment.Pass),
            (LegacyTaxonomyContract.FlowVerdict, "fail") => new(
                contract, legacyValue, QualityAssessment.Fail),
            (LegacyTaxonomyContract.FlowVerdict, "undetermined") => new(
                contract, legacyValue, QualityAssessment.Inconclusive),

            (LegacyTaxonomyContract.AttackVerdict, "pass") => new(
                contract, legacyValue, QualityAssessment.Pass),
            (LegacyTaxonomyContract.AttackVerdict, "finding") => new(
                contract, legacyValue, QualityAssessment.Fail),
            (LegacyTaxonomyContract.AttackVerdict, "not-applicable") => new(
                contract, legacyValue, QualityAssessment.NotApplicable),
            (LegacyTaxonomyContract.AttackVerdict, "not-yet-checked") => new(
                contract, legacyValue, QualityAssessment.NotAssessed),

            (LegacyTaxonomyContract.ChangeSummary, "no-quality-delta") => new(
                contract, legacyValue, Change: "no-observed-delta"),
            (LegacyTaxonomyContract.ChangeSummary, "improved") => new(
                contract, legacyValue, Change: "improved"),
            (LegacyTaxonomyContract.ChangeSummary, "neutral") => new(
                contract, legacyValue, Change: "unchanged"),
            (LegacyTaxonomyContract.ChangeSummary, "regression") => new(
                contract, legacyValue, Change: "regressed"),

            (LegacyTaxonomyContract.ChangeAspect, "good") => new(
                contract, legacyValue, QualityAssessment.Pass),
            (LegacyTaxonomyContract.ChangeAspect, "mixed") => new(
                contract, legacyValue, QualityAssessment.Concern),
            (LegacyTaxonomyContract.ChangeAspect, "concerning") => new(
                contract, legacyValue, QualityAssessment.Fail),
            (LegacyTaxonomyContract.ChangeAspect, "unknown") => new(
                contract, legacyValue, QualityAssessment.Inconclusive),

            (LegacyTaxonomyContract.FindingState, "open") => new(
                contract, legacyValue, Lifecycle: "open"),
            (LegacyTaxonomyContract.FindingState, "accepted") => new(
                contract, legacyValue, Lifecycle: "accepted-risk"),
            (LegacyTaxonomyContract.FindingState, "accepted-risk") => new(
                contract, legacyValue, Lifecycle: "accepted-risk"),
            (LegacyTaxonomyContract.FindingState, "waived") => new(
                contract, legacyValue, Lifecycle: "waived"),
            (LegacyTaxonomyContract.FindingState, "falsePositive") => new(
                contract, legacyValue, Lifecycle: "false-positive"),
            (LegacyTaxonomyContract.FindingState, "false-positive") => new(
                contract, legacyValue, Lifecycle: "false-positive"),
            (LegacyTaxonomyContract.FindingState, "resolved") => new(
                contract, legacyValue, Lifecycle: "resolved"),
            _ => throw new ArgumentException(
                $"Unknown {contract} legacy value '{legacyValue}'.", nameof(legacyValue)),
        };
    }

    public static QualityEvidence MapEvidence(string id, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(evidence);
        var contentHash = "sha256:" +
                          Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        try
        {
            using var parsed = JsonDocument.Parse(evidence);
            return new QualityEvidence(
                id,
                QualityEvidenceKind.ToolResult,
                "Legacy structured evidence.",
                ContentHash: contentHash,
                MediaType: "application/json",
                Content: parsed.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new QualityEvidence(
                id,
                QualityEvidenceKind.Document,
                "Legacy text evidence.",
                ContentHash: contentHash,
                MediaType: "text/plain",
                Content: JsonSerializer.SerializeToElement(evidence));
        }
    }
}
