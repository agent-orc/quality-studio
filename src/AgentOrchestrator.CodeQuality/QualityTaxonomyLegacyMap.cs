using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Core catalogue term ids, so adapters do not spell them by hand.</summary>
public static class CoreTerms
{
    public static class Assessment
    {
        public const string Pass = "pass";
        public const string Concern = "concern";
        public const string Fail = "fail";
        public const string Inconclusive = "inconclusive";
        public const string NotApplicable = "not-applicable";
        public const string NotAssessed = "not-assessed";
    }

    public static class EvidenceStatus
    {
        public const string Available = "available";
        public const string Partial = "partial";
        public const string Unavailable = "unavailable";
    }

    public static class Change
    {
        public const string Improved = "improved";
        public const string Regressed = "regressed";
        public const string Mixed = "mixed";
        public const string Unchanged = "unchanged";
        public const string NoObservedDelta = "no-observed-delta";
        public const string Inconclusive = "inconclusive";
    }

    public static class Decision
    {
        public const string Allow = "allow";
        public const string Warn = "warn";
        public const string Block = "block";
        public const string Defer = "defer";
    }

    public static class ProducerKind
    {
        public const string Agent = "agent";
        public const string DeterministicSensor = "deterministic-sensor";
        public const string Human = "human";
        public const string Imported = "imported";
        public const string Unknown = "unknown";
    }

    public static class Lifecycle
    {
        public const string Open = "open";
        public const string AcceptedRisk = "accepted-risk";
        public const string Waived = "waived";
        public const string FalsePositive = "false-positive";
        public const string Resolved = "resolved";
    }

    public static class EvidenceKind
    {
        public const string SourceCode = "source-code";
        public const string TestResult = "test-result";
        public const string RuntimeMeasurement = "runtime-measurement";
        public const string ToolResult = "tool-result";
        public const string Artifact = "artifact";
        public const string Document = "document";
        public const string HumanAttestation = "human-attestation";
    }

    public static class Severity
    {
        public const string Critical = "critical";
        public const string High = "high";
        public const string Medium = "medium";
        public const string Low = "low";
        public const string Info = "info";
    }
}

/// <summary>
/// A legacy verdict projected onto the core axes it actually spans. A policy disposition is
/// carried separately from the evidence assessment and never replaces it.
/// </summary>
public sealed record LegacyAssessment(
    string Assessment,
    string EvidenceStatus,
    string? Decision = null,
    string? PolicyRef = null);

public enum LegacyAspectDisposition
{
    /// <summary>The legacy id names a core aspect.</summary>
    Mapped,

    /// <summary>The legacy id described evidence coverage, not a quality dimension.</summary>
    MigratesToEvidenceStatus,

    /// <summary>The legacy id is a producer bucket; it becomes a producer-namespaced aspect, never a core one.</summary>
    ProducerScoped,

    /// <summary>No installed catalogue explains the id. It stays visible and out of core aggregates.</summary>
    Unrecognized,
}

public sealed record LegacyAspectMapping(
    string LegacyId,
    LegacyAspectDisposition Disposition,
    string? AspectId = null);

/// <summary>
/// Pure projections from the existing per-protocol vocabularies onto the core catalogue.
/// These are adapters: nothing here rewrites a historical file, and no absent, unknown, or
/// unavailable legacy value is turned into a pass.
/// </summary>
public static class QualityTaxonomyLegacyMap
{
    /// <summary>The retained security combination policy the legacy verdict belongs to.</summary>
    public const string SecurityPolicyRef = "security-sensor-agent-v1";

    /// <summary>The legacy aspect id that recorded sensor coverage rather than a quality dimension.</summary>
    public const string SensorAvailabilityAspect = "sensor-availability";

    /// <summary>The legacy aspect id used as a bucket for all deterministic analyzer output.</summary>
    public const string AnalyzerAspect = "analyzer";

    /// <summary>
    /// Security combined enforcement posture and evidence coverage in one enum.
    /// The projection splits them and keeps the legacy policy result as a decision.
    /// </summary>
    public static LegacyAssessment Security(SecurityVerdict verdict) => verdict switch
    {
        SecurityVerdict.Pass => new(
            CoreTerms.Assessment.Pass, CoreTerms.EvidenceStatus.Available, CoreTerms.Decision.Allow, SecurityPolicyRef),
        SecurityVerdict.Warn => new(
            CoreTerms.Assessment.Concern, CoreTerms.EvidenceStatus.Available, CoreTerms.Decision.Warn, SecurityPolicyRef),
        SecurityVerdict.Block => new(
            CoreTerms.Assessment.Fail, CoreTerms.EvidenceStatus.Available, CoreTerms.Decision.Block, SecurityPolicyRef),
        SecurityVerdict.Unavailable => new(
            CoreTerms.Assessment.Inconclusive, CoreTerms.EvidenceStatus.Unavailable, CoreTerms.Decision.Defer, SecurityPolicyRef),
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown security verdict."),
    };

    public static string Flow(FlowReviewVerdict verdict) => verdict switch
    {
        FlowReviewVerdict.Pass => CoreTerms.Assessment.Pass,
        FlowReviewVerdict.Fail => CoreTerms.Assessment.Fail,
        FlowReviewVerdict.Undetermined => CoreTerms.Assessment.Inconclusive,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown flow verdict."),
    };

    public static string Attack(AttackCoverageVerdict verdict) => verdict switch
    {
        AttackCoverageVerdict.Pass => CoreTerms.Assessment.Pass,
        AttackCoverageVerdict.Finding => CoreTerms.Assessment.Fail,
        AttackCoverageVerdict.NotApplicable => CoreTerms.Assessment.NotApplicable,
        AttackCoverageVerdict.NotYetChecked => CoreTerms.Assessment.NotAssessed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown attack coverage verdict."),
    };

    /// <summary>Change review summarizes a direction of delta, which belongs on the change axis.</summary>
    public static string ChangeSummary(ChangeReviewVerdict verdict) => verdict switch
    {
        ChangeReviewVerdict.NoQualityDelta => CoreTerms.Change.NoObservedDelta,
        ChangeReviewVerdict.Improved => CoreTerms.Change.Improved,
        ChangeReviewVerdict.Neutral => CoreTerms.Change.Unchanged,
        ChangeReviewVerdict.Regression => CoreTerms.Change.Regressed,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unknown change review verdict."),
    };

    /// <summary>
    /// Change aspect verdicts are a qualitative judgement, which belongs on the assessment axis.
    /// Values the catalogue does not explain are reported as unmapped rather than guessed.
    /// </summary>
    public static bool TryChangeAspect(string? verdict, out string assessment)
    {
        assessment = verdict switch
        {
            "good" => CoreTerms.Assessment.Pass,
            "mixed" => CoreTerms.Assessment.Concern,
            "concerning" => CoreTerms.Assessment.Fail,
            "unknown" => CoreTerms.Assessment.Inconclusive,
            "not-reviewed" => CoreTerms.Assessment.NotAssessed,
            _ => string.Empty,
        };
        return assessment.Length > 0;
    }

    /// <summary>Finding-state spellings, including the flow-review camel case drift.</summary>
    public static bool TryFindingState(string? state, out string lifecycle)
    {
        lifecycle = state switch
        {
            "open" => CoreTerms.Lifecycle.Open,
            "accepted" or "accepted-risk" or "acceptedRisk" => CoreTerms.Lifecycle.AcceptedRisk,
            "waived" => CoreTerms.Lifecycle.Waived,
            "false-positive" or "falsePositive" => CoreTerms.Lifecycle.FalsePositive,
            "resolved" => CoreTerms.Lifecycle.Resolved,
            _ => string.Empty,
        };
        return lifecycle.Length > 0;
    }

    public static string FindingState(FindingState state) => state switch
    {
        CodeQuality.FindingState.Open => CoreTerms.Lifecycle.Open,
        CodeQuality.FindingState.Accepted => CoreTerms.Lifecycle.AcceptedRisk,
        CodeQuality.FindingState.Waived => CoreTerms.Lifecycle.Waived,
        CodeQuality.FindingState.FalsePositive => CoreTerms.Lifecycle.FalsePositive,
        CodeQuality.FindingState.Resolved => CoreTerms.Lifecycle.Resolved,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown finding state."),
    };

    public static string Severity(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => CoreTerms.Severity.Critical,
        FindingSeverity.High => CoreTerms.Severity.High,
        FindingSeverity.Medium => CoreTerms.Severity.Medium,
        FindingSeverity.Low => CoreTerms.Severity.Low,
        FindingSeverity.Info => CoreTerms.Severity.Info,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown finding severity."),
    };

    /// <summary>
    /// Maps a legacy aspect id onto the namespaced core catalogue.
    /// <c>sensor-availability</c> was never a quality dimension and migrates to evidence status;
    /// the generic <c>analyzer</c> bucket becomes the aspect known for its rule, or a
    /// producer-namespaced aspect that can never be mistaken for a core term.
    /// </summary>
    public static LegacyAspectMapping Aspect(
        string legacyId,
        string? knownAspectForRule = null,
        string? producerId = null,
        QualityTaxonomyResolver? resolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyId);
        var effective = resolver ?? QualityTaxonomyResolver.Default;
        if (string.Equals(legacyId, SensorAvailabilityAspect, StringComparison.Ordinal))
            return new(legacyId, LegacyAspectDisposition.MigratesToEvidenceStatus);

        if (string.Equals(legacyId, AnalyzerAspect, StringComparison.Ordinal))
        {
            if (knownAspectForRule is { Length: > 0 } mapped && effective.ResolveAspect(mapped).IsKnown)
                return new(legacyId, LegacyAspectDisposition.Mapped, effective.ResolveAspect(mapped).CanonicalId);
            return producerId is { Length: > 0 } producer
                ? new(legacyId, LegacyAspectDisposition.ProducerScoped, $"{producer}:{AnalyzerAspect}.unmapped")
                : new(legacyId, LegacyAspectDisposition.Unrecognized);
        }

        var resolved = effective.ResolveAspect(legacyId);
        return resolved.Status switch
        {
            TaxonomyTermStatus.Canonical or TaxonomyTermStatus.Alias =>
                new(legacyId, LegacyAspectDisposition.Mapped, resolved.CanonicalId),
            TaxonomyTermStatus.Extension =>
                new(legacyId, LegacyAspectDisposition.ProducerScoped, resolved.CanonicalId),
            _ => new(legacyId, LegacyAspectDisposition.Unrecognized),
        };
    }

    /// <summary>
    /// Producer provenance for a legacy finding. Absence of a structured source is
    /// <c>unknown</c>, never <c>agent</c>; agent authorship must be proven by document context.
    /// </summary>
    public static ObservationFindingSource FindingSource(
        FindingSource? legacySource,
        bool agentAuthorshipProven = false,
        string? agentName = null)
    {
        if (legacySource is not null)
        {
            return new(
                CoreTerms.ProducerKind.DeterministicSensor,
                legacySource.Producer,
                legacySource.SensorId,
                legacySource.ProducerVersion);
        }

        return agentAuthorshipProven
            ? new(CoreTerms.ProducerKind.Agent, agentName ?? ObservationFindingSource.Self)
            : new(CoreTerms.ProducerKind.Unknown);
    }

    /// <summary>
    /// Turns the untyped legacy evidence string into a typed evidence item. Machine JSON becomes a
    /// <c>tool-result</c> with its media type and digest; anything else keeps its original text.
    /// Malformed or unknown content is preserved, never discarded.
    /// </summary>
    public static ObservationEvidence Evidence(
        string id,
        string evidence,
        string summary,
        string fallbackKind = CoreTerms.EvidenceKind.Document,
        ObservationLocator? locator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(evidence);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        if (TryParseJson(evidence, out var payload))
        {
            return new(id, CoreTerms.EvidenceKind.ToolResult, summary, locator,
                MediaType: "application/json", Content: payload, ContentHash: digest);
        }

        return new(id, fallbackKind, summary, locator,
            MediaType: "text/plain", Content: JsonValue.Create(evidence), ContentHash: digest);
    }

    /// <summary>A source location becomes a typed <c>source-code</c> evidence item.</summary>
    public static ObservationEvidence SourceEvidence(string id, FindingLocation location, string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(location);
        return new(id, CoreTerms.EvidenceKind.SourceCode, summary,
            new ObservationLocator(location.Path, location.SymbolId, location.Range));
    }

    private static bool TryParseJson(string value, out JsonNode? payload)
    {
        payload = null;
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return false;
        try
        {
            payload = JsonNode.Parse(value);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
