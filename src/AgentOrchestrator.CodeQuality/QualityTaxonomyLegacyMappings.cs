using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public enum QualityChange
{
    Improved,
    Regressed,
    Mixed,
    Unchanged,
    NoObservedDelta,
    Inconclusive,
}

public sealed record SecurityVerdictMapping(
    QualityAssessment Assessment,
    QualityEvidenceStatus EvidenceStatus,
    string? Decision,
    string? PolicyRef);

/// <summary>
/// Pure adapters from every current verdict/state spelling (dossier section 8, compatibility
/// mappings) to the core v1 taxonomy. These are read-time projections only; no writer is touched
/// by T1 (dossier section 10, T1 scope).
/// </summary>
public static class QualityTaxonomyLegacyMappings
{
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    public static SecurityVerdictMapping MapSecurityVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => new SecurityVerdictMapping(QualityAssessment.Pass, QualityEvidenceStatus.Available, "allow", null),
        "warn" => new SecurityVerdictMapping(QualityAssessment.Concern, QualityEvidenceStatus.Available, null, SecurityPolicyRef),
        "block" => new SecurityVerdictMapping(QualityAssessment.Fail, QualityEvidenceStatus.Available, "block", SecurityPolicyRef),
        "unavailable" => new SecurityVerdictMapping(QualityAssessment.Inconclusive, QualityEvidenceStatus.Unavailable, null, null),
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy security verdict."),
    };

    public static QualityAssessment MapFlowVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => QualityAssessment.Pass,
        "fail" => QualityAssessment.Fail,
        "undetermined" => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy flow verdict."),
    };

    public static QualityAssessment MapAttackVerdict(string legacyVerdict) => legacyVerdict switch
    {
        "pass" => QualityAssessment.Pass,
        "finding" => QualityAssessment.Fail,
        "not-applicable" => QualityAssessment.NotApplicable,
        "not-yet-checked" => QualityAssessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyVerdict), legacyVerdict, "Unknown legacy attack verdict."),
    };

    public static QualityChange MapChangeSummary(string legacySummary) => legacySummary switch
    {
        "no-quality-delta" => QualityChange.NoObservedDelta,
        "improved" => QualityChange.Improved,
        "neutral" => QualityChange.Unchanged,
        "regression" => QualityChange.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(legacySummary), legacySummary, "Unknown legacy change summary."),
    };

    public static QualityAssessment MapChangeAspect(string legacyAspectVerdict) => legacyAspectVerdict switch
    {
        "good" => QualityAssessment.Pass,
        "mixed" => QualityAssessment.Concern,
        "concerning" => QualityAssessment.Fail,
        "unknown" => QualityAssessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(
            nameof(legacyAspectVerdict), legacyAspectVerdict, "Unknown legacy change aspect verdict."),
    };

    /// <summary>Maps a legacy finding-state spelling to the core v1 lifecycle term id.</summary>
    public static string MapFindingLifecycleState(string legacyState) => legacyState switch
    {
        "open" => "open",
        "accepted" => "accepted-risk",
        "accepted-risk" => "accepted-risk",
        "waived" => "waived",
        "falsePositive" => "false-positive",
        "false-positive" => "false-positive",
        "resolved" => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(legacyState), legacyState, "Unknown legacy finding state."),
    };

    /// <summary>
    /// Adapts one legacy evidence string into a typed evidence item, preserving the original
    /// content and its digest (dossier section 8, evidence string rows). Valid JSON becomes typed
    /// <c>tool-result</c> evidence; anything else keeps <paramref name="fallbackKind"/>.
    /// </summary>
    public static QualityObservationEvidence MapEvidenceString(
        string evidenceId, string legacyEvidence, QualityEvidenceKind fallbackKind = QualityEvidenceKind.SourceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        ArgumentNullException.ThrowIfNull(legacyEvidence);
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacyEvidence)));

        if (TryParseJson(legacyEvidence, out var parsed))
        {
            var jsonLocator = JsonSerializer.SerializeToElement(new { mediaType = "application/json", raw = parsed });
            return new QualityObservationEvidence(
                evidenceId, QualityEvidenceKind.ToolResult, jsonLocator,
                "Preserved legacy tool-result JSON payload.", contentHash);
        }

        var textLocator = JsonSerializer.SerializeToElement(new { mediaType = "text/plain", text = legacyEvidence });
        return new QualityObservationEvidence(
            evidenceId, fallbackKind, textLocator, "Preserved legacy free-text evidence.", contentHash);
    }

    private static bool TryParseJson(string text, out JsonElement parsed)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            parsed = default;
            return false;
        }
    }
}
