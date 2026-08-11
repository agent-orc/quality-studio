using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public static class QualityDomainObservationAdapters
{
    public static QualityObservation FromReviewMeta(JsonObject metadata, string sourcePath, string importId)
    {
        var unit = metadata["unit"]?.AsObject() ?? throw new JsonException("Review metadata has no unit.");
        var reviewer = metadata["reviewer"]?.AsObject();
        var inputs = metadata["reviewInputs"]?.AsObject();
        var prompt = inputs?["prompt"]?.AsObject();
        var kind = metadata["kind"]?.GetValue<string>() ?? "unknown";
        var levelName = unit["level"]?.GetValue<string>() ?? "file";
        if (!Enum.TryParse<ReviewLevel>(levelName, true, out var level)) level = ReviewLevel.File;
        var runId = reviewer?["runId"]?.GetValue<string>() ?? importId;
        var observedAt = metadata["reviewedAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UnixEpoch;
        var observation = GeneralReviewObservationAdapter.Create(
            metadata,
            unit["id"]?.GetValue<string>() ?? "legacy:" + sourcePath,
            metadata["reviewedHash"]?["value"]?.GetValue<string>() ?? QualityObservationJson.Hash(sourcePath),
            inputs?["effectiveHash"]?["value"]?.GetValue<string>() ?? QualityObservationJson.Hash(importId),
            kind,
            level,
            runId,
            null,
            observedAt,
            reviewer?["agent"]?.GetValue<string>() ?? "unknown",
            null,
            reviewer?["model"]?.GetValue<string>(),
            reviewer?["model"]?.GetValue<string>(),
            null,
            null,
            SecurityEvidenceBundle.Empty,
            []);
        if (kind == "security" && metadata["security"]?["verdict"]?.GetValue<string>() is { } securityVerdict)
        {
            var mapped = QualityLegacyMappings.MapSecurityVerdict(securityVerdict);
            observation = observation with
            {
                Assessment = mapped.Assessment,
                EvidenceStatus = mapped.EvidenceStatus,
                Decision = mapped.Decision,
            };
        }
        var rawFindings = metadata["findings"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var migratedFindings = observation.Findings.Select((finding, index) => finding with
        {
            Source = index < rawFindings.Length
                ? LegacyFindingSource(rawFindings[index], metadata)
                : new QualityFindingSource(QualityProducerKind.Unknown, "unknown"),
        }).ToArray();
        var documentSensor = metadata["x-sensor-provenance"]?["sensorId"]?.GetValue<string>();
        var raw = JsonSerializer.SerializeToElement(metadata, QualityObservationJson.Options);
        return observation with
        {
            ObservationId = importId,
            Profile = observation.Profile with
            {
                Id = prompt?["id"]?.GetValue<string>() ?? observation.Profile.Id,
                Version = SemVer(prompt?["version"]?.GetValue<string>() ?? observation.Profile.Version),
                PromptHash = EnsureHash(
                    prompt?["contentHash"]?.GetValue<string>() ?? observation.Profile.PromptHash),
            },
            Producer = observation.Producer with
            {
                Kind = documentSensor is not null
                    ? QualityProducerKind.DeterministicSensor
                    : reviewer is null ? QualityProducerKind.Unknown : QualityProducerKind.Agent,
                Agent = documentSensor ?? observation.Producer.Agent,
            },
            Findings = migratedFindings,
            Legacy = new QualityLegacyReference(
                metadata["$schema"]?.GetValue<string>() ?? $"review-meta.v{metadata["schemaVersion"]?.GetValue<int>() ?? 1}",
                raw,
                sourcePath,
                "partial",
                QualityObservationJson.NoExtensions),
        };
    }

    private static QualityFindingSource LegacyFindingSource(JsonObject finding, JsonObject metadata)
    {
        var source = finding["source"]?.AsObject();
        var sourceKind = source?["kind"]?.GetValue<string>();
        var producer = source?["producer"]?.GetValue<string>() ?? source?["sensorId"]?.GetValue<string>();
        if (sourceKind is not null)
        {
            return sourceKind switch
            {
                "deterministic" or "deterministic-sensor" => new(
                    QualityProducerKind.DeterministicSensor, producer ?? "unknown"),
                "agent" => new(QualityProducerKind.Agent, producer ?? "self"),
                "human" => new(QualityProducerKind.Human, producer ?? "unknown"),
                "imported" => new(QualityProducerKind.Imported, producer ?? "unknown"),
                _ => new(QualityProducerKind.Unknown, producer ?? "unknown"),
            };
        }

        if (finding["evidence"]?.GetValue<string>() is { } evidence)
        {
            try
            {
                using var document = JsonDocument.Parse(evidence);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("source", out var evidenceSource) &&
                    evidenceSource.GetString() == "machine-sensor" &&
                    root.TryGetProperty("sensorId", out var sensorId) &&
                    !string.IsNullOrWhiteSpace(sensorId.GetString()))
                {
                    return new QualityFindingSource(
                        QualityProducerKind.DeterministicSensor, sensorId.GetString()!);
                }
            }
            catch (JsonException)
            {
                // Preserve malformed legacy evidence without claiming a producer.
            }
        }

        if (metadata["x-sensor-provenance"]?["sensorId"]?.GetValue<string>() is { } documentSensor)
            return new QualityFindingSource(QualityProducerKind.DeterministicSensor, documentSensor);
        return new QualityFindingSource(QualityProducerKind.Unknown, "unknown");
    }

    public static QualityObservation FromFlow(
        FlowReviewReport report,
        string sourcePath,
        string importId,
        string? provider = null,
        string? requestedModel = null,
        string? thinkingLevel = null,
        string? routingPolicyVersion = null)
    {
        var evidence = new List<QualityEvidence>();
        var findings = report.Findings.Select(finding =>
        {
            var refs = new List<string>();
            foreach (var step in finding.FlowPath.OrderBy(step => step.Order))
            {
                var id = $"ev-{evidence.Count + 1}";
                evidence.Add(new QualityEvidence(id, QualityEvidenceKind.SourceCode,
                    new QualityEvidenceLocator(step.Path, step.Symbol, step.Line, 1, step.Line, 1),
                    step.Action, null, null, null, QualityObservationJson.NoExtensions));
                refs.Add(id);
            }
            var weakest = finding.FlowPath[finding.WeakestPointIndex];
            var range = new FindingRange(new FindingPosition(weakest.Line, 1), new FindingPosition(weakest.Line, 1));
            return new QualityObservationFinding(
                finding.Id,
                QualityObservationIdentity.IssueId(weakest.Path, finding.RuleId, weakest.Symbol),
                QualityObservationIdentity.OccurrenceFingerprint(weakest.Path, finding.RuleId, range, weakest.Symbol),
                QualityObservationIdentity.FingerprintAlgorithm,
                [finding.Fingerprint],
                finding.RuleId,
                "security.business-logic",
                finding.Severity,
                finding.Title,
                finding.Description,
                finding.Recommendation,
                refs,
                new QualityFindingSource(QualityProducerKind.Agent, "self"),
                QualityObservationJson.NoExtensions);
        }).ToArray();
        var assessment = QualityLegacyMappings.MapFlowVerdict(
            JsonNamingPolicy.KebabCaseLower.ConvertName(report.Verdict.ToString()));
        return new QualityObservation(
            QualityObservation.SchemaId, 1, importId, report.Provenance.ReviewedAt,
            Core(), null,
            new QualitySubject("flow:" + report.Flow.Id, EnsureHash(report.Provenance.InputHash), "flow",
                QualityObservationJson.NoExtensions),
            new QualityProfile(report.Provenance.PromptId, SemVer(report.Provenance.PromptVersion),
                EnsureHash(report.Provenance.PromptHash), EnsureHash(report.Provenance.InputHash),
                "security", QualityObservationJson.NoExtensions),
            new QualityProducer(QualityProducerKind.Agent, Value(report.Provenance.Agent), Value(provider),
                Value(requestedModel), Value(report.Provenance.Model), Value(thinkingLevel), Value(routingPolicyVersion),
                report.Provenance.RunId, null, QualityObservationJson.NoExtensions),
            QualityEvidenceStatus.Available,
            evidence,
            [new QualityAspectObservation("security.business-logic", "Business logic", assessment, null,
                report.Summary, null, QualityObservationJson.NoExtensions)],
            assessment, null, null, findings,
            Legacy(report.Schema, report, sourcePath),
            QualityObservationJson.NoExtensions);
    }

    public static QualityObservation FromChange(ChangeReviewDocument document, string sourcePath, string importId)
    {
        var changeName = JsonNamingPolicy.KebabCaseLower.ConvertName(document.Verdict.ToString());
        var change = QualityLegacyMappings.MapChangeSummary(changeName);
        var aspects = document.Judgement.Aspects.Select(aspect => new QualityAspectObservation(
            QualityLegacyMappings.MapAspect(aspect.Id),
            aspect.Title,
            aspect.Verdict == "not-reviewed" ? QualityAssessment.NotAssessed :
                QualityLegacyMappings.MapChangeAspect(aspect.Verdict),
            null,
            aspect.Rationale,
            null,
            QualityObservationJson.NoExtensions)).ToArray();
        var producerKind = document.Judgement.Status == "not-run"
            ? QualityProducerKind.DeterministicSensor
            : QualityProducerKind.Agent;
        var extension = new Dictionary<string, JsonElement>
        {
            ["quality-studio/change-delta"] = JsonSerializer.SerializeToElement(document.Delta),
            ["quality-studio/economy"] = JsonSerializer.SerializeToElement(document.Economy),
        };
        return new QualityObservation(
            QualityObservation.SchemaId, 1, importId, document.ReviewedAt,
            Core(), null,
            new QualitySubject("change:" + document.ChangeSet.HeadCommit,
                QualityObservationJson.Hash($"{document.ChangeSet.BaseCommit}\0{document.ChangeSet.HeadCommit}\0{document.ChangeSet.MergeCommit}"),
                "change", QualityObservationJson.NoExtensions),
            new QualityProfile("change-review", "1.0.0", QualityObservationJson.Hash("change-review.v1"),
                QualityObservationJson.Hash(JsonSerializer.Serialize(document.ChangeSet)), "change",
                QualityObservationJson.NoExtensions),
            new QualityProducer(producerKind, Value(document.Judgement.Reviewer), "unknown", "unknown", "unknown",
                "unknown", "unknown", importId, null, QualityObservationJson.NoExtensions),
            document.Delta.Coverage.Status == "unavailable" ? QualityEvidenceStatus.Partial : QualityEvidenceStatus.Available,
            [new QualityEvidence("ev-1", QualityEvidenceKind.Artifact,
                new QualityEvidenceLocator(Reference: sourcePath), document.Summary,
                QualityObservationJson.Hash(JsonSerializer.Serialize(document.Delta)), "application/json",
                JsonSerializer.SerializeToElement(document.Delta), QualityObservationJson.NoExtensions)],
            aspects,
            QualityAssessment.Inconclusive,
            change,
            null,
            [],
            Legacy(document.Schema, document, sourcePath),
            extension);
    }

    public static QualityObservation FromAttack(AttackCoverageObservation attack, string sourcePath, string importId)
    {
        var evidence = attack.Evidence.Select((item, index) => new QualityEvidence(
            $"ev-{index + 1}",
            item.Kind.Contains("code", StringComparison.OrdinalIgnoreCase)
                ? QualityEvidenceKind.SourceCode
                : item.Kind.Contains("sensor", StringComparison.OrdinalIgnoreCase) ||
                  item.Kind.Contains("finding", StringComparison.OrdinalIgnoreCase)
                    ? QualityEvidenceKind.ToolResult
                    : QualityEvidenceKind.Artifact,
            new QualityEvidenceLocator(Reference: item.Reference),
            item.Summary,
            null,
            null,
            null,
            QualityObservationJson.NoExtensions)).ToArray();
        var assessment = QualityLegacyMappings.MapAttackVerdict(
            JsonNamingPolicy.KebabCaseLower.ConvertName(attack.Verdict.ToString()));
        var source = attack.Source switch
        {
            AttackCoverageSource.Agent => QualityProducerKind.Agent,
            AttackCoverageSource.DeterministicSensor => QualityProducerKind.DeterministicSensor,
            AttackCoverageSource.Human => QualityProducerKind.Human,
            _ => QualityProducerKind.Unknown,
        };
        var findings = new List<QualityObservationFinding>();
        if (attack.Verdict == AttackCoverageVerdict.Finding)
        {
            var legacyFingerprint = attack.FindingFingerprint;
            var occurrence = QualityObservationIdentity.OccurrenceFingerprint(
                attack.BoundaryId, attack.AttackId, null, null);
            findings.Add(new QualityObservationFinding(
                attack.FindingId ?? "of-attack-finding",
                QualityObservationIdentity.IssueId(attack.BoundaryId, attack.AttackId, null),
                occurrence,
                QualityObservationIdentity.FingerprintAlgorithm,
                legacyFingerprint is null ? [] : [legacyFingerprint],
                attack.AttackId,
                "security.attack-coverage",
                FindingSeverity.High,
                "Attack coverage finding",
                attack.Reasoning,
                "Review the linked attack evidence and remediate the weakest boundary.",
                evidence.Select(item => item.Id).ToArray(),
                new QualityFindingSource(source, source == QualityProducerKind.Agent ? "self" : attack.Reviewer.Agent),
                QualityObservationJson.NoExtensions));
        }
        var extensions = new Dictionary<string, JsonElement>
        {
            ["quality-studio/attack-assessment-id"] = JsonSerializer.SerializeToElement(attack.AssessmentId),
            ["quality-studio/attack-catalogue-version"] = JsonSerializer.SerializeToElement(attack.CatalogueVersion),
            ["quality-studio/attack-catalogue-entry-hash"] = JsonSerializer.SerializeToElement(attack.CatalogueEntryHash),
            ["quality-studio/attack-boundary-definition-hash"] = JsonSerializer.SerializeToElement(attack.BoundaryDefinitionHash),
            ["quality-studio/attack-token-cost"] = JsonSerializer.SerializeToElement(attack.TokenCost),
            ["quality-studio/attack-deterministic-input"] = JsonSerializer.SerializeToElement(attack.DeterministicSensorInput),
        };
        return new QualityObservation(
            QualityObservation.SchemaId, 1, importId, attack.CheckedAt,
            Core(), null,
            new QualitySubject("boundary:" + attack.BoundaryId, EnsureHash(attack.CoveredCodeHash), "boundary",
                QualityObservationJson.NoExtensions),
            new QualityProfile("attack-coverage", SemVer(attack.PromptVersion), EnsureHash(attack.PromptHash),
                EnsureHash(attack.CatalogueEntryHash), "security", QualityObservationJson.NoExtensions),
            new QualityProducer(source, Value(attack.Reviewer.Agent), "unknown", Value(attack.Reviewer.Model),
                Value(attack.Reviewer.Model), Value(attack.Reviewer.ThinkingLevel), "unknown",
                attack.AssessmentId, null, QualityObservationJson.NoExtensions),
            QualityEvidenceStatus.Available,
            evidence,
            [new QualityAspectObservation("security.attack-coverage", "Attack coverage", assessment, null,
                attack.Reasoning, null, QualityObservationJson.NoExtensions)],
            assessment, null, null, findings,
            Legacy("attack-coverage.v1", attack, sourcePath),
            extensions);
    }

    public static string ImportId(string kind, string sourcePath, string payload) =>
        "observation-sha256:" + QualityObservationJson.Hash(
            $"quality-studio/import/v1\0{kind}\0{sourcePath.Replace('\\', '/')}\0{QualityObservationJson.Hash(payload)}")
            ["sha256:".Length..];

    private static QualityLegacyReference Legacy<T>(string schema, T value, string sourcePath) => new(
        schema,
        JsonSerializer.SerializeToElement(value, QualityObservationJson.Options),
        sourcePath,
        "partial",
        QualityObservationJson.NoExtensions);

    private static QualityCatalogueReference Core() => new(
        QualityTaxonomyCatalogue.CoreId, QualityTaxonomyCatalogue.CoreVersion, QualityTaxonomyCatalogue.CoreDigest);

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    private static string EnsureHash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;
    private static string SemVer(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")
            ? value
            : "1.0.0";
}
