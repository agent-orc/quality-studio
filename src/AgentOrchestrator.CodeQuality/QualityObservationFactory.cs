using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public static class QualityObservationFactory
{
    public static QualityObservationDocument FromReview(
        JsonObject response,
        ReviewRequest request,
        IReviewAgent agent,
        ReviewAgentResult agentResult,
        ReviewUsageEntry usage,
        string relativePath,
        string unitId,
        string reviewedHash,
        string reviewInputsHash,
        ResolvedInputs inputs,
        IReadOnlyList<SubjectInputHash> subjectInputs,
        SecurityEvidenceBundle securityEvidence,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        var taxonomy = CoreQualityCatalogue.Instance;
        var observationId = QualityObservationLedger.CreateObservationId(
            agentResult.RunId, unitId, request.Kind, reviewedHash, reviewInputsHash, taxonomy.Digest);
        var provider = Known(agentResult.Provider, request.Provider, agent.Provider);
        var requestedModel = Known(agentResult.RequestedModel, request.RequestedModel, agent.Model);
        var effectiveModel = Known(agentResult.EffectiveModel, usage.EffectiveModel, usage.Model);
        var thinkingLevel = Known(agentResult.ThinkingLevel, request.ThinkingLevel, agent.ThinkingLevel);
        var routePolicyVersion = Known(
            agentResult.RoutePolicyVersion, request.RoutePolicyVersion, agent.RoutePolicyVersion);
        var inputHashes = subjectInputs.ToDictionary(input => input.Path, input => NormalizeHash(input.ContentHash),
            StringComparer.Ordinal);
        var evidence = new List<QualityEvidence>();
        var findings = new List<QualityObservationFinding>();
        var findingNodes = response["findings"]!.AsArray().OfType<JsonObject>().ToArray();
        for (var findingIndex = 0; findingIndex < findingNodes.Length; findingIndex++)
        {
            var finding = findingNodes[findingIndex];
            var evidenceRefs = new List<string>();
            var locations = finding["locations"]!.AsArray().OfType<JsonObject>().ToArray();
            for (var locationIndex = 0; locationIndex < locations.Length; locationIndex++)
            {
                var location = locations[locationIndex];
                var evidenceId = $"ev-{findingIndex + 1}-loc-{locationIndex + 1}";
                var path = location["path"]!.GetValue<string>();
                var range = location["range"] as JsonObject;
                var start = range?["start"] as JsonObject;
                var end = range?["end"] as JsonObject;
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    CoreQualityTerms.EvidenceKinds.SourceCode,
                    $"Source location supporting finding '{finding["id"]!.GetValue<string>()}'.",
                    new QualityEvidenceLocator(
                        path,
                        location["symbolId"]?.GetValue<string>(),
                        StartLine: start?["line"]?.GetValue<int>(),
                        StartColumn: start?["column"]?.GetValue<int>(),
                        EndLine: end?["line"]?.GetValue<int>(),
                        EndColumn: end?["column"]?.GetValue<int>()),
                    ContentHash: inputHashes.GetValueOrDefault(path)));
                evidenceRefs.Add(evidenceId);
            }

            var evidenceText = finding["evidence"]?.GetValue<string>();
            var source = ResolveFindingSource(finding, evidenceText);
            if (evidenceText is not null)
            {
                var legacyEvidence = LegacyTaxonomyMapping.MapEvidence(
                    $"ev-{findingIndex + 1}-legacy", evidenceText).Evidence;
                evidence.Add(legacyEvidence);
                evidenceRefs.Add(legacyEvidence.Id);
            }

            var fingerprint = finding["fingerprint"]!.GetValue<string>();
            var occurrenceFingerprint = finding["occurrenceFingerprint"]?.GetValue<string>() ?? fingerprint;
            var fingerprintAlgorithm = finding["fingerprintAlgorithm"]?.GetValue<string>() ??
                                       FindingIdentity.Canonicalization;
            var legacyFingerprints = finding["legacyFingerprints"]?.AsArray()
                .Select(item => item!.GetValue<string>()).ToArray() ?? [fingerprint];
            findings.Add(new QualityObservationFinding(
                finding["id"]!.GetValue<string>(),
                occurrenceFingerprint,
                fingerprintAlgorithm,
                legacyFingerprints,
                finding["ruleId"]!.GetValue<string>(),
                LegacyTaxonomyMapping.MapAspectId(finding["aspect"]!.GetValue<string>()),
                finding["severity"]!.GetValue<string>(),
                finding["title"]!.GetValue<string>(),
                finding["description"]!.GetValue<string>(),
                finding["recommendation"]!.GetValue<string>(),
                evidenceRefs,
                source,
                finding["issueId"]?.GetValue<string>()));
        }

        var aspects = response["aspects"]!.AsArray().OfType<JsonObject>()
            .Where(aspect => !string.Equals(
                aspect["id"]?.GetValue<string>(), "sensor-availability", StringComparison.Ordinal))
            .Select(aspect =>
            {
                var grade = aspect["grade"]!.AsObject();
                return new QualityObservationAspect(
                    LegacyTaxonomyMapping.MapAspectId(aspect["id"]!.GetValue<string>()),
                    CoreQualityTerms.Axes.Assessment,
                    AssessmentFromGrade(grade),
                    grade["rationale"]!.GetValue<string>(),
                    new QualityObservationGrade(
                        grade["score"]!.GetValue<int>(), grade["band"]!.GetValue<string>()));
            }).ToArray();
        var overallGrade = response["grade"]!.AsObject();
        var assessment = AssessmentFromGrade(overallGrade);
        var evidenceStatus = inputs.Complete
            ? CoreQualityTerms.EvidenceStatuses.Available
            : CoreQualityTerms.EvidenceStatuses.Partial;
        QualityPolicyDecision? decision = null;
        if (request.Kind == "security" && securityEvidence.Sensors.Count > 0)
        {
            var security = LegacyTaxonomyMapping.MapSecurityVerdict(
                SecurityEvidenceBundle.VerdictName(securityEvidence.Verdict));
            assessment = security.Value;
            evidenceStatus = security.EvidenceStatus!;
            decision = new QualityPolicyDecision(security.Decision!, security.PolicyRef!);
        }

        var completeProvenance = new[]
            {
                provider, requestedModel, effectiveModel, thinkingLevel, routePolicyVersion,
            }
            .All(value => !string.Equals(value, CoreQualityTerms.ProducerKinds.Unknown, StringComparison.Ordinal));
        return new QualityObservationDocument(
            QualityObservationDocument.SchemaId,
            QualityObservationDocument.CurrentSchemaVersion,
            observationId,
            observedAt.ToUniversalTime(),
            taxonomy.Reference,
            [],
            new QualityObservationSubject(
                unitId,
                request.Level.ToString().ToLowerInvariant(),
                NormalizeHash(reviewedHash),
                NormalizeHash(reviewInputsHash),
                relativePath),
            new QualityObservationProfile(
                $"file-{request.Kind}-review",
                "1.0.0",
                NormalizeHash(ReviewPromptBuilder.TemplateHash(request.Kind)),
                NormalizeHash(reviewInputsHash)),
            new QualityObservationProducer(
                CoreQualityTerms.ProducerKinds.Agent,
                agent.AgentName,
                provider,
                requestedModel,
                effectiveModel,
                thinkingLevel,
                routePolicyVersion,
                agentResult.RunId,
                ReviewRunId: request.ReviewRunId,
                UsageRunId: usage.RunId),
            evidenceStatus,
            evidence,
            aspects,
            assessment,
            findings,
            inputs.Complete && completeProvenance ? "complete" : "partial",
            decision);
    }

    private static string AssessmentFromGrade(JsonObject grade) => grade["band"]!.GetValue<string>() switch
    {
        "A" or "B" => CoreQualityTerms.Assessments.Pass,
        "C" => CoreQualityTerms.Assessments.Concern,
        "D" or "F" => CoreQualityTerms.Assessments.Fail,
        _ => CoreQualityTerms.Assessments.Inconclusive,
    };

    private static QualityFindingSource ResolveFindingSource(JsonObject finding, string? evidence)
    {
        if (finding["source"] is JsonObject sourceNode)
        {
            var kind = sourceNode["kind"]?.GetValue<string>() switch
            {
                "deterministic" => CoreQualityTerms.ProducerKinds.DeterministicSensor,
                "human" => CoreQualityTerms.ProducerKinds.Human,
                "imported" => CoreQualityTerms.ProducerKinds.Imported,
                "agent" => CoreQualityTerms.ProducerKinds.Agent,
                _ => CoreQualityTerms.ProducerKinds.Unknown,
            };
            var producer = sourceNode["sensorId"]?.GetValue<string>() ??
                           sourceNode["producer"]?.GetValue<string>() ?? "unknown";
            return new QualityFindingSource(kind, producer);
        }
        if (evidence is not null)
        {
            try
            {
                using var parsed = JsonDocument.Parse(evidence);
                var root = parsed.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("source", out var source) &&
                    string.Equals(source.GetString(), "machine-sensor", StringComparison.Ordinal) &&
                    root.TryGetProperty("sensorId", out var sensorId) &&
                    !string.IsNullOrWhiteSpace(sensorId.GetString()))
                    return new QualityFindingSource(
                        CoreQualityTerms.ProducerKinds.DeterministicSensor, sensorId.GetString()!);
            }
            catch (JsonException)
            {
                // The preserved evidence mapping handles malformed text; it proves no sensor identity.
            }
        }
        return new QualityFindingSource(CoreQualityTerms.ProducerKinds.Agent, "self");
    }

    private static string Known(params string?[] candidates) => candidates
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
        ?? CoreQualityTerms.ProducerKinds.Unknown;

    private static string NormalizeHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value : "sha256:" + value;
}
