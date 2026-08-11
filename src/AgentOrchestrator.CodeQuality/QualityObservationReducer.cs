using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationSelectionTarget(
    string UnitId,
    string SubjectHash,
    string ProfileId,
    string ProfileVersion,
    string PromptHash,
    string ReviewInputsHash,
    string TaxonomyId,
    string TaxonomyVersion,
    string TaxonomyDigest);

public sealed record QualityModelRecord(
    string EffectiveModel,
    string RequestedModel,
    string ThinkingLevel,
    string ProfileId,
    string ProfileVersion,
    int Samples,
    string Comparability,
    double? AverageScore,
    IReadOnlyDictionary<string, int> Assessments,
    int Findings);

public sealed record QualityUnknownAspect(
    string AspectId,
    string TaxonomyId,
    string TaxonomyVersion,
    int Observations);

public sealed record QualityObservationReduction(
    string SelectionPolicy,
    IReadOnlyList<QualityObservationDocument> Current,
    IReadOnlyList<QualityModelRecord> Models,
    IReadOnlyList<QualityUnknownAspect> UnknownAspects);

/// <summary>One policy for current projection, model records, and unknown-term handling.</summary>
public static class QualityObservationReducer
{
    public const string SelectionPolicy = "quality-observation-current@1";
    public const string ProjectionExtension = "quality-studio:review-meta-v2-projection";

    public static QualityObservationDocument? SelectCurrent(
        IEnumerable<QualityObservationDocument> observations,
        QualityObservationSelectionTarget target) =>
        observations.Where(observation =>
                string.Equals(observation.Subject.UnitId, target.UnitId, StringComparison.Ordinal) &&
                string.Equals(observation.Subject.ManifestHash, PrefixHash(target.SubjectHash), StringComparison.Ordinal) &&
                string.Equals(observation.Profile.Id, target.ProfileId, StringComparison.Ordinal) &&
                string.Equals(observation.Profile.Version, target.ProfileVersion, StringComparison.Ordinal) &&
                string.Equals(observation.Profile.PromptHash, target.PromptHash, StringComparison.Ordinal) &&
                string.Equals(observation.Profile.ReviewInputsHash, PrefixHash(target.ReviewInputsHash), StringComparison.Ordinal) &&
                string.Equals(observation.Taxonomy.Id, target.TaxonomyId, StringComparison.Ordinal) &&
                string.Equals(observation.Taxonomy.Version, target.TaxonomyVersion, StringComparison.Ordinal) &&
                string.Equals(observation.Taxonomy.Digest, target.TaxonomyDigest, StringComparison.Ordinal))
            .OrderByDescending(observation => observation.ObservedAt)
            .ThenByDescending(observation => observation.ObservationId, StringComparer.Ordinal)
            .FirstOrDefault();

    public static QualityObservationReduction Reduce(IEnumerable<QualityObservationDocument> source)
    {
        var observations = source.OrderBy(item => item.ObservedAt)
            .ThenBy(item => item.ObservationId, StringComparer.Ordinal).ToArray();
        var current = observations.GroupBy(
                item => ComparisonKey(item),
                StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.ObservedAt)
                .ThenByDescending(item => item.ObservationId, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.Subject.UnitId, StringComparer.Ordinal)
            .ThenBy(item => item.Profile.Id, StringComparer.Ordinal)
            .ToArray();
        var comparability = observations
            .GroupBy(item => $"{item.Profile.Id}\0{item.Profile.Version}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Comparability(group.ToArray()), StringComparer.Ordinal);
        var models = observations.GroupBy(item => new
            {
                item.Producer.EffectiveModel,
                item.Producer.ThinkingLevel,
                item.Profile.Id,
                item.Profile.Version,
            })
            .Select(group => new QualityModelRecord(
                group.Key.EffectiveModel,
                RequestedModel(group),
                group.Key.ThinkingLevel,
                group.Key.Id,
                group.Key.Version,
                group.Count(),
                comparability[$"{group.Key.Id}\0{group.Key.Version}"],
                AverageScore(group),
                group.GroupBy(item => item.Assessment, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal),
                group.Sum(item => item.Findings.Count)))
            .OrderBy(item => item.EffectiveModel, StringComparer.Ordinal)
            .ThenBy(item => item.ThinkingLevel, StringComparer.Ordinal)
            .ThenBy(item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
        var unknown = observations.SelectMany(observation => observation.Aspects
                .Where(aspect => !QualityTaxonomyCatalogue.IsInstalledAspect(
                    aspect.AspectId, [QualityTaxonomyCatalogue.CoreDocument]))
                .Select(aspect => new
                {
                    aspect.AspectId,
                    observation.Taxonomy.Id,
                    observation.Taxonomy.Version,
                }))
            .GroupBy(item => new { item.AspectId, item.Id, item.Version })
            .Select(group => new QualityUnknownAspect(
                group.Key.AspectId, group.Key.Id, group.Key.Version, group.Count()))
            .OrderBy(item => item.AspectId, StringComparer.Ordinal)
            .ToArray();
        return new QualityObservationReduction(SelectionPolicy, current, models, unknown);
    }

    public static JsonObject? ProjectCurrentSidecar(QualityObservationDocument observation)
    {
        if (!observation.Extensions.TryGetValue(ProjectionExtension, out var projection) ||
            projection.ValueKind != JsonValueKind.Object)
            return null;
        return JsonNode.Parse(projection.GetRawText())?.AsObject();
    }

    private static string Comparability(IReadOnlyList<QualityObservationDocument> observations)
    {
        if (observations.Count < 2) return "incomplete";
        if (observations.Any(item =>
                IsUnknown(item.Producer.EffectiveModel) || IsUnknown(item.Producer.ThinkingLevel) ||
                string.IsNullOrWhiteSpace(item.Subject.ManifestHash) || string.IsNullOrWhiteSpace(item.Profile.PromptHash) ||
                string.IsNullOrWhiteSpace(item.Profile.ReviewInputsHash) || string.IsNullOrWhiteSpace(item.Taxonomy.Digest)))
            return "incomplete";
        var routes = observations.GroupBy(
                item => $"{item.Producer.EffectiveModel}\0{item.Producer.ThinkingLevel}",
                StringComparer.Ordinal)
            .ToArray();
        if (routes.Length < 2) return "incomplete";
        var comparableSets = routes.Select(route => string.Join('\n', route
                .Select(ComparisonKey)
                .Order(StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return comparableSets == 1 ? "controlled" : "observational";
    }

    private static string ComparisonKey(QualityObservationDocument observation) => string.Join('\0',
        observation.Subject.UnitId,
        observation.Subject.ManifestHash,
        observation.Profile.Id,
        observation.Profile.Version,
        observation.Profile.PromptHash,
        observation.Profile.ReviewInputsHash,
        observation.Taxonomy.Id,
        observation.Taxonomy.Version,
        observation.Taxonomy.Digest,
        observation.EvidenceStatus,
        EvidenceApplicability(observation));

    private static string EvidenceApplicability(QualityObservationDocument observation) => string.Join('|',
        observation.Evidence.Select(evidence => JsonSerializer.Serialize(new
            {
                evidence.Kind,
                evidence.Locator.Path,
                evidence.Locator.SymbolId,
                evidence.Locator.ArtifactRef,
                evidence.Locator.Uri,
                evidence.ContentHash,
            })).Order(StringComparer.Ordinal));

    private static string RequestedModel(IEnumerable<QualityObservationDocument> observations)
    {
        var requested = observations.Select(item => item.Producer.RequestedModel)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return requested.Length == 1 ? requested[0] : "mixed";
    }

    private static double? AverageScore(IEnumerable<QualityObservationDocument> observations)
    {
        var scores = observations.SelectMany(item => QualityTaxonomyCatalogue.SelectInstalledAspects(
                item, [QualityTaxonomyCatalogue.CoreDocument]))
            .Where(item => item.Grade is not null)
            .Select(item => item.Grade!.Score)
            .ToArray();
        return scores.Length == 0 ? null : Math.Round(scores.Average(), 2);
    }

    private static bool IsUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "unknown", StringComparison.Ordinal);

    private static string PrefixHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value : "sha256:" + value;
}
