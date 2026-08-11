using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationSelectionContext(
    string? UnitId = null,
    string? Kind = null,
    string? SubjectManifestHash = null,
    string? ProfileId = null,
    string? ProfileVersion = null,
    string? PromptHash = null,
    string? TaxonomyDigest = null);

public enum QualityModelComparability { Controlled, Observational, Incomplete }

public sealed record QualityModelAggregate(
    string EffectiveModel,
    IReadOnlyList<string> RequestedModels,
    bool RequestedEffectiveMismatch,
    string ThinkingLevel,
    string ProfileId,
    string ProfileVersion,
    int SampleCount,
    double? AverageScore,
    IReadOnlyDictionary<QualityAssessment, int> Assessments,
    int FindingCount,
    QualityModelComparability Comparability);

public static class QualityObservationReducer
{
    public const string SelectionPolicy = "quality-studio-current-observation@1";

    public static IReadOnlyList<QualityObservation> SelectCurrent(
        IEnumerable<QualityObservation> observations,
        QualityObservationSelectionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var filtered = observations.Where(observation => Matches(observation, context)).ToArray();
        return filtered
            .GroupBy(observation => (observation.Subject.UnitId, observation.Profile.Kind))
            .Select(group => SelectCoordinate(group, context))
            .Where(observation => observation is not null)
            .Cast<QualityObservation>()
            .OrderBy(observation => observation.Subject.UnitId, StringComparer.Ordinal)
            .ThenBy(observation => observation.Profile.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<QualityModelAggregate> AggregateByModel(
        IEnumerable<QualityObservation> observations)
    {
        var all = observations.ToArray();
        var comparability = Compare(all);
        return all.GroupBy(observation => (
                observation.Producer.EffectiveModel,
                observation.Producer.ThinkingLevel,
                observation.Profile.Id,
                observation.Profile.Version))
            .Select(group =>
            {
                var scores = group.Select(Score)
                    .Where(score => score.HasValue)
                    .Select(score => score!.Value)
                    .ToArray();
                var requestedModels = group.Select(item => item.Producer.RequestedModel)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return new QualityModelAggregate(
                    group.Key.EffectiveModel,
                    requestedModels,
                    requestedModels.Any(requested =>
                        !IsUnknown(requested) && requested != group.Key.EffectiveModel),
                    group.Key.ThinkingLevel,
                    group.Key.Id,
                    group.Key.Version,
                    group.Count(),
                    scores.Length == 0 ? null : Math.Round(scores.Average(), 2),
                    group.GroupBy(item => item.Assessment).ToDictionary(item => item.Key, item => item.Count()),
                    group.Sum(item => item.Findings.Count),
                    comparability);
            })
            .OrderBy(item => item.EffectiveModel, StringComparer.Ordinal)
            .ThenBy(item => item.ThinkingLevel, StringComparer.Ordinal)
            .ThenBy(item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
    }

    public static JsonObject CreateReviewMetaProjection(
        QualityObservation observation,
        JsonObject? compatibilityBase = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var root = compatibilityBase?.DeepClone().AsObject() ?? new JsonObject();
        root["$schema"] ??= ReviewMetaDocument.SchemaId;
        root["schemaVersion"] ??= ReviewMetaDocument.CurrentSchemaVersion;
        root["unit"] ??= new JsonObject
        {
            ["id"] = observation.Subject.UnitId,
            ["adapter"] = Adapter(observation.Subject.UnitId),
            ["level"] = observation.Subject.Scope,
            ["path"] = ".",
            ["displayName"] = observation.Subject.UnitId,
        };
        root["reviewedAt"] = observation.ObservedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        root["kind"] = observation.Profile.Kind;
        if (root["reviewer"] is not JsonObject reviewer)
        {
            reviewer = new JsonObject();
            root["reviewer"] = reviewer;
        }
        reviewer["agent"] = observation.Producer.Agent;
        reviewer["model"] = observation.Producer.EffectiveModel;
        reviewer["runId"] = observation.Producer.RunId;
        root["reviewedHash"] ??= new JsonObject
        {
            ["algorithm"] = "sha256",
            ["canonicalization"] = "quality-studio-subject-manifest-v1",
            ["value"] = observation.Subject.ManifestHash["sha256:".Length..],
        };
        var score = Score(observation) ?? 0;
        root["grade"] = new JsonObject
        {
            ["score"] = score,
            ["band"] = QualityReportBuilder.Grade(score),
            ["rationale"] = $"Projected by {SelectionPolicy}; unrecognized aspects are excluded.",
        };
        root["summary"] ??= $"{observation.Assessment.ToString().ToLowerInvariant()} observation projection.";
        root["aspects"] = new JsonArray(observation.Aspects.Select(aspect => (JsonNode)new JsonObject
        {
            ["id"] = aspect.AspectId,
            ["title"] = aspect.Title ?? aspect.AspectId,
            ["grade"] = aspect.Grade is null ? null : new JsonObject
            {
                ["score"] = aspect.Grade.Score,
                ["band"] = aspect.Grade.Band,
                ["rationale"] = aspect.Rationale,
            },
            ["extensions"] = JsonSerializer.SerializeToNode(aspect.Extensions),
        }).ToArray());
        root["findings"] = new JsonArray(observation.Findings.Select(finding =>
            (JsonNode)FindingProjection(finding, observation.Evidence)).ToArray());
        return root;
    }

    public static int? Score(QualityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Extensions.TryGetValue("quality-studio/overall-grade", out var overall) &&
            overall.ValueKind == JsonValueKind.Object &&
            overall.TryGetProperty("score", out var scoreNode) &&
            scoreNode.TryGetInt32(out var score) && score is >= 0 and <= 100)
            return score;
        var scores = observation.Aspects
            .Where(aspect => QualityTaxonomyCatalogue.IsCoreAspect(aspect.AspectId) && aspect.Grade is not null)
            .Select(aspect => aspect.Grade!.Score).ToArray();
        return scores.Length == 0
            ? null
            : (int)Math.Round(scores.Average(), MidpointRounding.AwayFromZero);
    }

    private static JsonObject FindingProjection(
        QualityObservationFinding finding,
        IReadOnlyList<QualityEvidence> evidence)
    {
        var locations = evidence.Where(item => finding.EvidenceRefs.Contains(item.Id, StringComparer.Ordinal))
            .Where(item => !string.IsNullOrWhiteSpace(item.Locator.Path))
            .Select(item => (JsonNode)new JsonObject
            {
                ["path"] = item.Locator.Path,
                ["symbolId"] = item.Locator.SymbolId,
                ["range"] = item.Locator.StartLine is null ? null : new JsonObject
                {
                    ["start"] = new JsonObject
                    {
                        ["line"] = item.Locator.StartLine,
                        ["column"] = item.Locator.StartColumn ?? 1,
                    },
                    ["end"] = new JsonObject
                    {
                        ["line"] = item.Locator.EndLine ?? item.Locator.StartLine,
                        ["column"] = item.Locator.EndColumn ?? item.Locator.StartColumn ?? 1,
                    },
                },
            }).ToArray();
        return new JsonObject
        {
            ["id"] = finding.ObservationFindingId,
            ["aspect"] = finding.AspectId,
            ["severity"] = JsonNamingPolicy.KebabCaseLower.ConvertName(finding.Severity.ToString()),
            ["title"] = finding.Title,
            ["description"] = finding.Description,
            ["recommendation"] = finding.Recommendation,
            ["locations"] = new JsonArray(locations),
            ["fingerprint"] = finding.FingerprintAliases.FirstOrDefault() ?? finding.OccurrenceFingerprint,
            ["ruleId"] = finding.RuleRef,
            ["source"] = new JsonObject
            {
                ["kind"] = finding.Source.Kind == QualityProducerKind.DeterministicSensor ? "deterministic" :
                    JsonNamingPolicy.KebabCaseLower.ConvertName(finding.Source.Kind.ToString()),
                ["sensorId"] = finding.Source.Kind == QualityProducerKind.DeterministicSensor
                    ? finding.Source.ProducerRef : null,
                ["producer"] = finding.Source.ProducerRef,
            },
        };
    }

    private static QualityObservation? SelectCoordinate(
        IEnumerable<QualityObservation> observations,
        QualityObservationSelectionContext? context)
    {
        var candidates = observations.ToArray();
        if (candidates.Length == 0) return null;
        if (context is null || context.SubjectManifestHash is null && context.ProfileId is null &&
            context.ProfileVersion is null && context.PromptHash is null && context.TaxonomyDigest is null)
        {
            var coordinate = candidates
                .GroupBy(Coordinate)
                .OrderByDescending(group => group.Max(item => item.ObservedAt))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .First().Key;
            candidates = candidates.Where(item => Coordinate(item) == coordinate).ToArray();
        }
        return candidates.OrderByDescending(item => item.ObservedAt)
            .ThenBy(item => item.ObservationId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool Matches(QualityObservation observation, QualityObservationSelectionContext? context) =>
        context is null ||
        (context.UnitId is null || observation.Subject.UnitId == context.UnitId) &&
        (context.Kind is null || observation.Profile.Kind == context.Kind) &&
        (context.SubjectManifestHash is null || observation.Subject.ManifestHash == context.SubjectManifestHash) &&
        (context.ProfileId is null || observation.Profile.Id == context.ProfileId) &&
        (context.ProfileVersion is null || observation.Profile.Version == context.ProfileVersion) &&
        (context.PromptHash is null || observation.Profile.PromptHash == context.PromptHash) &&
        (context.TaxonomyDigest is null || observation.Taxonomy.Digest == context.TaxonomyDigest);

    private static QualityModelComparability Compare(IReadOnlyList<QualityObservation> observations)
    {
        if (observations.Count == 0 || observations.Any(item =>
                item.EvidenceStatus != QualityEvidenceStatus.Available ||
                IsUnknown(item.Producer.RequestedModel) ||
                IsUnknown(item.Producer.EffectiveModel) || IsUnknown(item.Producer.ThinkingLevel) ||
                IsUnknown(item.Producer.Provider) || IsUnknown(item.Producer.RoutePolicyVersion)))
            return QualityModelComparability.Incomplete;
        var coordinates = observations.Select(item => string.Join('\0',
            item.Subject.ManifestHash,
            item.Profile.Id,
            item.Profile.Version,
            item.Profile.PromptHash,
            item.Profile.ReviewInputsHash,
            item.Taxonomy.Digest,
            EvidenceApplicability(item))).Distinct(StringComparer.Ordinal).Count();
        return coordinates == 1 ? QualityModelComparability.Controlled : QualityModelComparability.Observational;
    }

    private static string Coordinate(QualityObservation observation) => string.Join('\0',
        observation.Subject.ManifestHash,
        observation.Profile.Id,
        observation.Profile.Version,
        observation.Profile.PromptHash,
        observation.Profile.ReviewInputsHash,
        observation.Taxonomy.Digest);

    private static string EvidenceApplicability(QualityObservation observation) => string.Join('|',
        observation.Evidence.Select(item => $"{item.Kind}:{item.Locator.Path}:{item.Locator.Reference}")
            .Order(StringComparer.Ordinal));

    private static bool IsUnknown(string value) => string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);

    private static string Adapter(string unitId)
    {
        var segments = unitId.Split('/');
        return segments.Length > 1 && segments[1] is "angular" or "dotnet" or "generic"
            ? segments[1]
            : "generic";
    }
}
