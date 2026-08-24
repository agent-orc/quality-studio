using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Projects one review-meta document onto the immutable observation contract. The projection is a
/// pure adapter: it reads the sidecar that was just built and never rewrites it.
/// </summary>
public static class ReviewObservationProjection
{
    /// <summary>
    /// The versioned rule that turns evidenced findings into an assessment. It is recorded so a
    /// later change to the rule is visible instead of silently reinterpreting stored observations.
    /// </summary>
    public const string AssessmentRuleId = "quality-studio-review-assessment-v1";

    private const string MachineSensorEvidenceSource = "machine-sensor";

    public static QualityObservation FromReviewMeta(
        JsonObject meta,
        ObservationProducer producer,
        DateTimeOffset recordedAt,
        QualityTaxonomyCatalogue? taxonomy = null,
        ObservationLegacyOrigin? legacy = null)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(producer);
        var catalogue = taxonomy ?? QualityTaxonomyCatalogue.Core;
        var resolver = catalogue == QualityTaxonomyCatalogue.Core
            ? QualityTaxonomyResolver.Default
            : new QualityTaxonomyResolver(catalogue);
        var unitId = Text(meta["unit"]?["id"]) ?? throw new ArgumentException("Review metadata has no unit id.", nameof(meta));
        var kind = Text(meta["kind"]) ?? throw new ArgumentException("Review metadata has no kind.", nameof(meta));
        var subjectHash = Prefixed(Text(meta["reviewedHash"]?["value"]));
        var reviewInputsHash = Prefixed(Text(meta["reviewInputs"]?["effectiveHash"]?["value"]));
        var security = meta["security"] as JsonObject;
        var evidenceStatus = EvidenceStatus(meta, security);

        var evidence = new List<ObservationEvidence>();
        var findings = ProjectFindings(meta, resolver, security is not null, producer, evidence).ToArray();
        var aspects = ProjectAspects(meta, resolver, findings).ToArray();

        return new QualityObservation
        {
            ObservationId = ObservationIdentity.Compute(
                ReviewRouteProvenance.OrUnknown(producer.RunId), unitId, kind,
                subjectHash, reviewInputsHash, catalogue.Digest),
            RecordedAt = recordedAt.ToUniversalTime(),
            Taxonomy = catalogue.Reference,
            Subject = new(
                unitId,
                subjectHash,
                Text(meta["unit"]?["path"]),
                Text(meta["unit"]?["level"]),
                Kind: kind),
            Profile = new(
                Text(meta["reviewInputs"]?["prompt"]?["id"]) ?? $"file-{kind}-review",
                Text(meta["reviewInputs"]?["prompt"]?["version"]) ?? "1.0.0",
                Text(meta["reviewInputs"]?["prompt"]?["contentHash"]),
                reviewInputsHash),
            Producer = producer,
            EvidenceStatus = evidenceStatus,
            Assessment = Assess(findings.Select(finding => finding.Severity), evidenceStatus),
            Grade = Grade(meta["grade"] as JsonObject),
            Summary = Text(meta["summary"]),
            Evidence = evidence,
            Aspects = aspects,
            Findings = findings,
            PolicyOutcomes = PolicyOutcomes(security),
            Legacy = legacy,
        };
    }

    /// <summary>
    /// The assessment an observation carries. Unavailable evidence is inconclusive and is never
    /// converted to a pass; a policy decision never substitutes for this judgement.
    /// </summary>
    public static string Assess(IEnumerable<string> severities, string evidenceStatus)
    {
        if (string.Equals(evidenceStatus, CoreTerms.EvidenceStatus.Unavailable, StringComparison.Ordinal))
            return CoreTerms.Assessment.Inconclusive;
        var present = severities.ToArray();
        if (present.Any(severity => severity is CoreTerms.Severity.Critical or CoreTerms.Severity.High))
            return CoreTerms.Assessment.Fail;
        return present.Any(severity => severity is CoreTerms.Severity.Medium or CoreTerms.Severity.Low)
            ? CoreTerms.Assessment.Concern
            : CoreTerms.Assessment.Pass;
    }

    private static string EvidenceStatus(JsonObject meta, JsonObject? security)
    {
        if (Text(security?["verdict"]) is { } verdict &&
            Enum.TryParse<SecurityVerdict>(verdict, ignoreCase: true, out var parsed))
        {
            var mapped = QualityTaxonomyLegacyMap.Security(parsed).EvidenceStatus;
            if (!string.Equals(mapped, CoreTerms.EvidenceStatus.Available, StringComparison.Ordinal)) return mapped;
        }

        return meta["reviewInputs"]?["complete"]?.GetValue<bool>() == false
            ? CoreTerms.EvidenceStatus.Partial
            : CoreTerms.EvidenceStatus.Available;
    }

    private static IReadOnlyList<ObservationPolicyOutcome> PolicyOutcomes(JsonObject? security)
    {
        if (Text(security?["verdict"]) is not { } verdict ||
            !Enum.TryParse<SecurityVerdict>(verdict, ignoreCase: true, out var parsed))
            return [];
        var mapped = QualityTaxonomyLegacyMap.Security(parsed);
        return [new(Text(security!["combinationRule"]) ?? QualityTaxonomyLegacyMap.SecurityPolicyRef,
            mapped.Decision!, LegacyValue: verdict)];
    }

    private static IEnumerable<ObservationFinding> ProjectFindings(
        JsonObject meta,
        QualityTaxonomyResolver resolver,
        bool sensorFindingsMayBeInlined,
        ObservationProducer producer,
        List<ObservationEvidence> evidence)
    {
        foreach (var finding in meta["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var references = new List<string>();
            foreach (var location in finding["locations"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var item = QualityTaxonomyLegacyMap.SourceEvidence(
                    $"ev-{evidence.Count + 1}", Location(location), Text(finding["title"]) ?? "Reported location.");
                evidence.Add(item);
                references.Add(item.Id);
            }

            var sensor = SensorEvidence(Text(finding["evidence"]));
            if (Text(finding["evidence"]) is { } raw)
            {
                var item = QualityTaxonomyLegacyMap.Evidence(
                    $"ev-{evidence.Count + 1}", raw, Text(finding["title"]) ?? "Reported evidence.");
                evidence.Add(item);
                references.Add(item.Id);
            }

            var aspect = QualityTaxonomyLegacyMap.Aspect(Text(finding["aspect"]) ?? "analyzer", resolver: resolver);
            yield return new(
                Text(finding["id"]) ?? $"of-{references.Count}",
                aspect.AspectId ?? Text(finding["aspect"]) ?? "analyzer",
                Text(finding["severity"]) ?? CoreTerms.Severity.Info,
                Source(finding, sensor, sensorFindingsMayBeInlined, producer),
                OccurrenceFingerprint: Text(finding["fingerprint"]),
                FingerprintAlgorithm: Text(finding["fingerprint"]) is null ? null : FindingIdentity.Canonicalization,
                RuleRef: Text(finding["ruleId"]),
                Title: Text(finding["title"]),
                Description: Text(finding["description"]),
                Recommendation: Text(finding["recommendation"]),
                EvidenceRefs: references.Count == 0 ? null : references);
        }
    }

    /// <summary>
    /// Producer provenance for one legacy finding. A structured source or an exact machine-sensor
    /// evidence payload proves a deterministic sensor. Otherwise the finding is only attributed to
    /// the agent when no sensor could have contributed to this document; ambiguity stays unknown.
    /// </summary>
    private static ObservationFindingSource Source(
        JsonObject finding,
        (string SensorId, string? SensorVersion)? sensor,
        bool sensorFindingsMayBeInlined,
        ObservationProducer producer)
    {
        if (finding["source"] is JsonObject structured)
        {
            return new(CoreTerms.ProducerKind.DeterministicSensor,
                Text(structured["producer"]), Text(structured["sensorId"]), Text(structured["producerVersion"]));
        }

        if (sensor is { } exact)
            return new(CoreTerms.ProducerKind.DeterministicSensor, exact.SensorId, exact.SensorId, exact.SensorVersion);

        return sensorFindingsMayBeInlined ||
               !string.Equals(producer.Kind, CoreTerms.ProducerKind.Agent, StringComparison.Ordinal)
            ? new(CoreTerms.ProducerKind.Unknown)
            : new(CoreTerms.ProducerKind.Agent, producer.Agent ?? ObservationFindingSource.Self);
    }

    private static (string SensorId, string? SensorVersion)? SensorEvidence(string? evidence)
    {
        if (evidence is null) return null;
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(evidence);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return parsed is JsonObject payload &&
               string.Equals(Text(payload["source"]), MachineSensorEvidenceSource, StringComparison.Ordinal) &&
               Text(payload["sensorId"]) is { } sensorId
            ? (sensorId, Text(payload["sensorVersion"]))
            : null;
    }

    private static IEnumerable<ObservationAspect> ProjectAspects(
        JsonObject meta,
        QualityTaxonomyResolver resolver,
        IReadOnlyList<ObservationFinding> findings)
    {
        foreach (var aspect in meta["aspects"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var legacyId = Text(aspect["id"]);
            if (legacyId is null) continue;
            var mapped = QualityTaxonomyLegacyMap.Aspect(legacyId, resolver: resolver);
            if (mapped.Disposition == LegacyAspectDisposition.MigratesToEvidenceStatus) continue;
            var aspectId = mapped.AspectId ?? legacyId;
            var severities = findings
                .Where(finding => string.Equals(finding.AspectId, aspectId, StringComparison.Ordinal))
                .Select(finding => finding.Severity);
            yield return new(
                aspectId,
                Assess(severities, CoreTerms.EvidenceStatus.Available),
                Text(aspect["title"]),
                Text(aspect["grade"]?["rationale"]),
                Grade(aspect["grade"] as JsonObject));
        }
    }

    private static ObservationGrade? Grade(JsonObject? grade) =>
        grade?["score"] is null || grade["band"] is null
            ? null
            : new(grade["score"]!.GetValue<int>(), grade["band"]!.GetValue<string>(), Text(grade["rationale"]));

    private static FindingLocation Location(JsonObject location) => new(
        Text(location["path"]) ?? ".",
        location["range"] is JsonObject range
            ? new FindingRange(Position(range["start"] as JsonObject), Position(range["end"] as JsonObject))
            : null,
        Text(location["symbolId"]));

    private static FindingPosition Position(JsonObject? position) =>
        new(position?["line"]?.GetValue<int>() ?? 1, position?["column"]?.GetValue<int>() ?? 1);

    private static string Prefixed(string? hash) =>
        hash is null ? throw new ArgumentException("Review metadata has no subject or input hash.")
            : hash.StartsWith("sha256:", StringComparison.Ordinal) ? hash : "sha256:" + hash;

    private static string? Text(JsonNode? node) =>
        node?.GetValueKind() == System.Text.Json.JsonValueKind.String ? node.GetValue<string>() : null;
}
