using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

public sealed record LegacyTaxonomyProjection(
    string Axis,
    string Value,
    string? EvidenceStatus = null,
    string? Decision = null,
    string? PolicyRef = null);

public sealed record LegacyEvidenceProjection(
    QualityEvidence Evidence,
    bool ParsedAsJson);

public static class LegacyTaxonomyMapping
{
    public const string SecurityPolicyRef = "security-sensor-agent@1";

    public static LegacyTaxonomyProjection MapSecurityVerdict(string value) => value switch
    {
        "pass" => new(CoreQualityTerms.Axes.Assessment, CoreQualityTerms.Assessments.Pass,
            CoreQualityTerms.EvidenceStatuses.Available, CoreQualityTerms.Decisions.Allow, SecurityPolicyRef),
        "warn" => new(CoreQualityTerms.Axes.Assessment, CoreQualityTerms.Assessments.Concern,
            CoreQualityTerms.EvidenceStatuses.Available, CoreQualityTerms.Decisions.Warn, SecurityPolicyRef),
        "block" => new(CoreQualityTerms.Axes.Assessment, CoreQualityTerms.Assessments.Fail,
            CoreQualityTerms.EvidenceStatuses.Available, CoreQualityTerms.Decisions.Block, SecurityPolicyRef),
        "unavailable" => new(CoreQualityTerms.Axes.Assessment, CoreQualityTerms.Assessments.Inconclusive,
            CoreQualityTerms.EvidenceStatuses.Unavailable, CoreQualityTerms.Decisions.Defer, SecurityPolicyRef),
        _ => Unknown(nameof(value), value, "security verdict"),
    };

    public static LegacyTaxonomyProjection MapFlowVerdict(string value) => value switch
    {
        "pass" => Assessment(CoreQualityTerms.Assessments.Pass),
        "fail" => Assessment(CoreQualityTerms.Assessments.Fail),
        "undetermined" => Assessment(CoreQualityTerms.Assessments.Inconclusive),
        _ => Unknown(nameof(value), value, "flow verdict"),
    };

    public static LegacyTaxonomyProjection MapAttackVerdict(string value) => value switch
    {
        "pass" => Assessment(CoreQualityTerms.Assessments.Pass),
        "finding" => Assessment(CoreQualityTerms.Assessments.Fail),
        "not-applicable" => Assessment(CoreQualityTerms.Assessments.NotApplicable),
        "not-yet-checked" => Assessment(CoreQualityTerms.Assessments.NotAssessed),
        _ => Unknown(nameof(value), value, "attack verdict"),
    };

    public static LegacyTaxonomyProjection MapChangeSummary(string value) => value switch
    {
        "no-quality-delta" => Change(CoreQualityTerms.Changes.NoObservedDelta),
        "improved" => Change(CoreQualityTerms.Changes.Improved),
        "neutral" => Change(CoreQualityTerms.Changes.Unchanged),
        "regression" => Change(CoreQualityTerms.Changes.Regressed),
        _ => Unknown(nameof(value), value, "change summary"),
    };

    public static LegacyTaxonomyProjection MapChangeAspect(string value) => value switch
    {
        "good" => Assessment(CoreQualityTerms.Assessments.Pass),
        "mixed" => Assessment(CoreQualityTerms.Assessments.Concern),
        "concerning" => Assessment(CoreQualityTerms.Assessments.Fail),
        "unknown" => Assessment(CoreQualityTerms.Assessments.Inconclusive),
        _ => Unknown(nameof(value), value, "change aspect verdict"),
    };

    public static LegacyTaxonomyProjection MapLifecycle(string value) => value switch
    {
        "open" => Lifecycle(CoreQualityTerms.Lifecycles.Open),
        "accepted" or "accepted-risk" => Lifecycle(CoreQualityTerms.Lifecycles.AcceptedRisk),
        "waived" => Lifecycle(CoreQualityTerms.Lifecycles.Waived),
        "falsePositive" or "false-positive" => Lifecycle(CoreQualityTerms.Lifecycles.FalsePositive),
        "resolved" => Lifecycle(CoreQualityTerms.Lifecycles.Resolved),
        _ => Unknown(nameof(value), value, "finding lifecycle"),
    };

    public static string MapAspectId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return CoreQualityCatalogue.Instance.TryResolveAspect(value, out var aspect)
            ? aspect!.Id
            : value;
    }

    public static LegacyEvidenceProjection MapEvidence(string id, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(evidence);
        var bytes = Encoding.UTF8.GetBytes(evidence);
        var contentHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            using var parsed = JsonDocument.Parse(evidence);
            return new LegacyEvidenceProjection(new QualityEvidence(
                id,
                CoreQualityTerms.EvidenceKinds.ToolResult,
                "Preserved structured legacy evidence.",
                ContentHash: contentHash,
                MediaType: "application/json",
                Raw: parsed.RootElement.Clone()), true);
        }
        catch (JsonException)
        {
            return new LegacyEvidenceProjection(new QualityEvidence(
                id,
                CoreQualityTerms.EvidenceKinds.Document,
                string.IsNullOrWhiteSpace(evidence) ? "Preserved empty legacy evidence." : evidence,
                ContentHash: contentHash,
                MediaType: "text/plain",
                Raw: JsonSerializer.SerializeToElement(evidence)), false);
        }
    }

    private static LegacyTaxonomyProjection Assessment(string value) =>
        new(CoreQualityTerms.Axes.Assessment, value);

    private static LegacyTaxonomyProjection Change(string value) =>
        new(CoreQualityTerms.Axes.Change, value);

    private static LegacyTaxonomyProjection Lifecycle(string value) =>
        new(CoreQualityTerms.Axes.Lifecycle, value);

    private static LegacyTaxonomyProjection Unknown(string parameterName, string value, string contract) =>
        throw new ArgumentOutOfRangeException(parameterName, value, $"Unknown legacy {contract} value.");
}
