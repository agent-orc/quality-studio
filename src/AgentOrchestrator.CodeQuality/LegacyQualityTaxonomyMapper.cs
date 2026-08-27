using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public enum LegacyQualityVocabulary
{
    SecurityVerdict,
    FlowVerdict,
    AttackVerdict,
    ChangeSummary,
    ChangeAspect,
    FindingState,
}

public sealed record LegacyQualityMapping(
    LegacyQualityVocabulary Vocabulary,
    string LegacyValue,
    QualityAssessment? Assessment = null,
    QualityEvidenceStatus? EvidenceStatus = null,
    QualityPolicyDecision? Decision = null,
    QualityChange? Change = null,
    QualityLifecycle? Lifecycle = null);

/// <summary>
/// Pure, context-free mappings from existing protocol spellings to the core v1 axes.
/// Historical documents are not mutated and every result retains the original value.
/// </summary>
public static class LegacyQualityTaxonomyMapper
{
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    public static LegacyQualityMapping Map(LegacyQualityVocabulary vocabulary, string value) => vocabulary switch
    {
        LegacyQualityVocabulary.SecurityVerdict => MapSecurityVerdict(value),
        LegacyQualityVocabulary.FlowVerdict => MapFlowVerdict(value),
        LegacyQualityVocabulary.AttackVerdict => MapAttackVerdict(value),
        LegacyQualityVocabulary.ChangeSummary => MapChangeSummary(value),
        LegacyQualityVocabulary.ChangeAspect => MapChangeAspect(value),
        LegacyQualityVocabulary.FindingState => MapFindingState(value),
        _ => throw new ArgumentOutOfRangeException(nameof(vocabulary), vocabulary, "Unknown legacy vocabulary."),
    };

    public static LegacyQualityMapping MapSecurityVerdict(string value) => value switch
    {
        "pass" => new(
            LegacyQualityVocabulary.SecurityVerdict,
            value,
            QualityAssessment.Pass,
            QualityEvidenceStatus.Available,
            new QualityPolicyDecision(QualityDecision.Allow, SecurityPolicyRef)),
        "warn" => new(
            LegacyQualityVocabulary.SecurityVerdict,
            value,
            QualityAssessment.Concern,
            Decision: new QualityPolicyDecision(QualityDecision.Warn, SecurityPolicyRef)),
        "block" => new(
            LegacyQualityVocabulary.SecurityVerdict,
            value,
            QualityAssessment.Fail,
            Decision: new QualityPolicyDecision(QualityDecision.Block, SecurityPolicyRef)),
        "unavailable" => new(
            LegacyQualityVocabulary.SecurityVerdict,
            value,
            QualityAssessment.Inconclusive,
            QualityEvidenceStatus.Unavailable),
        _ => Unknown(LegacyQualityVocabulary.SecurityVerdict, value),
    };

    public static LegacyQualityMapping MapFlowVerdict(string value) => value switch
    {
        "pass" => Assessment(LegacyQualityVocabulary.FlowVerdict, value, QualityAssessment.Pass),
        "fail" => Assessment(LegacyQualityVocabulary.FlowVerdict, value, QualityAssessment.Fail),
        "undetermined" => Assessment(LegacyQualityVocabulary.FlowVerdict, value, QualityAssessment.Inconclusive),
        _ => Unknown(LegacyQualityVocabulary.FlowVerdict, value),
    };

    public static LegacyQualityMapping MapAttackVerdict(string value) => value switch
    {
        "pass" => Assessment(LegacyQualityVocabulary.AttackVerdict, value, QualityAssessment.Pass),
        "finding" => Assessment(LegacyQualityVocabulary.AttackVerdict, value, QualityAssessment.Fail),
        "not-applicable" => Assessment(LegacyQualityVocabulary.AttackVerdict, value, QualityAssessment.NotApplicable),
        "not-yet-checked" => Assessment(LegacyQualityVocabulary.AttackVerdict, value, QualityAssessment.NotAssessed),
        _ => Unknown(LegacyQualityVocabulary.AttackVerdict, value),
    };

    public static LegacyQualityMapping MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => Change(LegacyQualityVocabulary.ChangeSummary, value, QualityChange.NoObservedDelta),
        "improved" => Change(LegacyQualityVocabulary.ChangeSummary, value, QualityChange.Improved),
        "neutral" => Change(LegacyQualityVocabulary.ChangeSummary, value, QualityChange.Unchanged),
        "regression" => Change(LegacyQualityVocabulary.ChangeSummary, value, QualityChange.Regressed),
        _ => Unknown(LegacyQualityVocabulary.ChangeSummary, value),
    };

    public static LegacyQualityMapping MapChangeAspect(string value) => value switch
    {
        "good" => Assessment(LegacyQualityVocabulary.ChangeAspect, value, QualityAssessment.Pass),
        "mixed" => Assessment(LegacyQualityVocabulary.ChangeAspect, value, QualityAssessment.Concern),
        "concerning" => Assessment(LegacyQualityVocabulary.ChangeAspect, value, QualityAssessment.Fail),
        "unknown" => Assessment(LegacyQualityVocabulary.ChangeAspect, value, QualityAssessment.Inconclusive),
        _ => Unknown(LegacyQualityVocabulary.ChangeAspect, value),
    };

    public static LegacyQualityMapping MapFindingState(string value) => value switch
    {
        "open" => Lifecycle(value, QualityLifecycle.Open),
        "accepted" or "accepted-risk" => Lifecycle(value, QualityLifecycle.AcceptedRisk),
        "waived" => Lifecycle(value, QualityLifecycle.Waived),
        "falsePositive" or "false-positive" => Lifecycle(value, QualityLifecycle.FalsePositive),
        "resolved" => Lifecycle(value, QualityLifecycle.Resolved),
        _ => Unknown(LegacyQualityVocabulary.FindingState, value),
    };

    public static QualityObservationEvidence MapEvidence(string id, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);

        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        try
        {
            using var parsed = JsonDocument.Parse(value);
            return new QualityObservationEvidence(
                id,
                QualityEvidenceKind.ToolResult,
                new QualityEvidenceLocator(ArtifactRef: "legacy:inline"),
                "Legacy JSON evidence preserved without semantic reinterpretation.",
                contentHash,
                "application/json",
                parsed.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new QualityObservationEvidence(
                id,
                QualityEvidenceKind.Document,
                new QualityEvidenceLocator(ArtifactRef: "legacy:inline"),
                "Legacy text evidence preserved verbatim.",
                contentHash,
                "text/plain",
                JsonSerializer.SerializeToElement(value));
        }
    }

    private static LegacyQualityMapping Assessment(
        LegacyQualityVocabulary vocabulary,
        string value,
        QualityAssessment assessment) => new(vocabulary, value, Assessment: assessment);

    private static LegacyQualityMapping Change(
        LegacyQualityVocabulary vocabulary,
        string value,
        QualityChange change) => new(vocabulary, value, Change: change);

    private static LegacyQualityMapping Lifecycle(string value, QualityLifecycle lifecycle) =>
        new(LegacyQualityVocabulary.FindingState, value, Lifecycle: lifecycle);

    private static LegacyQualityMapping Unknown(LegacyQualityVocabulary vocabulary, string value) =>
        throw new ArgumentException($"Unknown {vocabulary} value '{value}'.", nameof(value));
}
