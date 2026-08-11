using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Additive adapters from durable domain contracts into the common observation ledger.</summary>
public static class QualityDomainObservationAdapters
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtensions =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public static QualityObservationDocument FromFlow(FlowReviewReport report, string sourcePath)
    {
        var legacy = report.Verdict.ToString().ToLowerInvariant();
        var mapping = QualityLegacyMapper.Map(LegacyQualityVocabulary.FlowVerdict, legacy);
        var evidence = report.Findings.SelectMany((finding, findingIndex) =>
                finding.FlowPath.Select((step, stepIndex) => new QualityEvidence(
                    $"flow-{findingIndex + 1}-step-{stepIndex + 1}",
                    "source-code",
                    new QualityEvidenceLocator(step.Path, step.Symbol, step.Line),
                    step.Action,
                    Extensions: NoExtensions)))
            .ToArray();
        var findings = report.Findings.Select((finding, index) =>
        {
            var occurrence = FindingIdentity.OccurrenceFingerprint(finding.Fingerprint);
            return new QualityObservationFinding(
                finding.Id,
                FindingIdentity.IssueId(occurrence),
                occurrence,
                FindingIdentity.OccurrenceCanonicalization,
                finding.RuleId,
                "security.business-logic",
                finding.Severity.ToString().ToLowerInvariant(),
                finding.FlowPath.Select((_, stepIndex) => $"flow-{index + 1}-step-{stepIndex + 1}").ToArray(),
                new QualityFindingSource("agent", report.Provenance.Agent),
                [finding.Fingerprint]);
        }).ToArray();
        return Document(
            Id("flow", report.Provenance.RunId, report.Flow.Id, report.Provenance.InputHash),
            report.Provenance.ReviewedAt,
            new QualityObservationSubject(
                "flow:" + report.Flow.Id,
                report.Provenance.InputHash,
                "flow"),
            new QualityObservationProfile(
                report.Provenance.PromptId,
                report.Provenance.PromptVersion,
                report.Provenance.PromptHash,
                report.Provenance.InputHash),
            new QualityObservationProducer(
                "agent", report.Provenance.Agent, "unknown", report.Provenance.Model,
                report.Provenance.Model, "unknown", "unknown", report.Provenance.RunId,
                report.Provenance.RunId),
            "available",
            evidence,
            [new QualityObservationAspect("security.business-logic", mapping.Assessment!, report.Summary)],
            mapping.Assessment!,
            findings,
            new QualityObservationLegacy(
                "flow-review.v1", sourcePath, "complete",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["verdict"] = legacy }),
            extensions: DomainExtension("flow-review.v1", report));
    }

    public static QualityObservationDocument FromAttack(AttackCoverageObservation observation, string sourcePath)
    {
        var legacy = Kebab(observation.Verdict.ToString());
        var mapping = QualityLegacyMapper.Map(LegacyQualityVocabulary.AttackVerdict, legacy);
        var evidence = observation.Evidence.Select((item, index) => new QualityEvidence(
            $"attack-evidence-{index + 1}",
            EvidenceKind(item.Kind),
            new QualityEvidenceLocator(ArtifactRef: item.Reference),
            item.Summary,
            Extensions: NoExtensions)).ToArray();
        var findings = new List<QualityObservationFinding>();
        if (observation.Verdict == AttackCoverageVerdict.Finding)
        {
            var alias = observation.FindingFingerprint ?? Hash(
                $"attack-finding\0{observation.BoundaryId}\0{observation.AttackId}");
            var occurrence = FindingIdentity.OccurrenceFingerprint(alias);
            findings.Add(new QualityObservationFinding(
                observation.FindingId ?? "finding-" + occurrence[7..],
                FindingIdentity.IssueId(occurrence),
                occurrence,
                FindingIdentity.OccurrenceCanonicalization,
                observation.AttackId,
                "security.attack-coverage",
                "high",
                evidence.Select(item => item.Id).ToArray(),
                new QualityFindingSource(ProducerKind(observation.Source), observation.Reviewer.Agent),
                [alias]));
        }
        var producerKind = ProducerKind(observation.Source);
        return Document(
            Id("attack", observation.AssessmentId, observation.BoundaryId, observation.AttackId,
                observation.CatalogueEntryHash, observation.Reviewer.Agent),
            observation.CheckedAt,
            new QualityObservationSubject(
                "boundary:" + observation.BoundaryId,
                observation.CoveredCodeHash,
                "boundary"),
            new QualityObservationProfile(
                "attack-coverage-review",
                observation.PromptVersion,
                observation.PromptHash,
                Hash(string.Join('\0', observation.BoundaryDefinitionHash,
                    observation.CatalogueEntryHash, observation.AssessmentId))),
            new QualityObservationProducer(
                producerKind,
                observation.Reviewer.Agent,
                "unknown",
                observation.Reviewer.Model,
                observation.Reviewer.Model,
                observation.Reviewer.ThinkingLevel,
                "unknown",
                observation.AssessmentId,
                observation.AssessmentId),
            "available",
            evidence,
            [new QualityObservationAspect("security.attack-coverage", mapping.Assessment!, observation.Reasoning)],
            mapping.Assessment!,
            findings,
            new QualityObservationLegacy(
                "attack-coverage.v1", sourcePath, "complete",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["verdict"] = legacy,
                    ["assessmentId"] = observation.AssessmentId,
                    ["catalogueEntryHash"] = observation.CatalogueEntryHash,
                    ["catalogueVersion"] = observation.CatalogueVersion,
                }),
            extensions: DomainExtension("attack-coverage.v1", observation));
    }

    public static QualityObservationDocument FromChange(ChangeReviewDocument document, string sourcePath)
    {
        var legacyChange = Kebab(document.Verdict.ToString());
        var change = QualityLegacyMapper.Map(LegacyQualityVocabulary.ChangeSummary, legacyChange).Change!;
        var aspects = document.Judgement.Aspects.Select(aspect =>
        {
            var legacyAssessment = aspect.Verdict switch
            {
                "not-reviewed" => "unknown",
                _ => aspect.Verdict,
            };
            var assessment = QualityLegacyMapper.Map(
                LegacyQualityVocabulary.ChangeAspect, legacyAssessment).Assessment!;
            return new QualityObservationAspect(
                "change." + aspect.Id,
                assessment,
                aspect.Rationale,
                change);
        }).ToArray();
        var runId = document.ChangeSet.MergeCommit ?? document.ChangeSet.HeadCommit;
        var reviewer = string.IsNullOrWhiteSpace(document.Judgement.Reviewer)
            ? "unknown" : document.Judgement.Reviewer;
        return Document(
            Id("change", document.ChangeSet.BaseCommit, runId),
            document.ReviewedAt,
            new QualityObservationSubject(
                "change:" + runId,
                Hash(string.Join('\0', document.ChangeSet.TouchedFiles.Select(item => item.Path).Order(StringComparer.Ordinal))),
                "change-set"),
            new QualityObservationProfile(
                "change-review",
                "1.0.0",
                Hash("change-review-v1"),
                Hash(string.Join('\0', document.ChangeSet.BaseCommit, runId))),
            new QualityObservationProducer(
                reviewer == "none" ? "deterministic-sensor" : "agent",
                reviewer,
                "unknown",
                reviewer,
                reviewer,
                "unknown",
                "unknown",
                runId,
                runId),
            "available",
            [new QualityEvidence("change-document", "document",
                new QualityEvidenceLocator(ArtifactRef: sourcePath), document.Summary)],
            aspects,
            aspects.Any(item => item.Assessment == "fail") ? "fail"
                : aspects.Any(item => item.Assessment == "concern") ? "concern"
                : aspects.All(item => item.Assessment == "inconclusive") ? "inconclusive" : "pass",
            [],
            new QualityObservationLegacy(
                "change-review.v1", sourcePath, "complete",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["verdict"] = legacyChange }),
            change,
            DomainExtension("change-review.v1", document));
    }

    public static QualityObservationDocument FromReviewMeta(
        JsonObject metadata,
        string sourcePath,
        ReviewUsageEntry? usage)
    {
        var kind = Text(metadata["kind"]) ?? "unknown";
        var level = Text(metadata["unit"]?["level"]) ?? "unknown";
        var unitId = Text(metadata["unit"]?["id"]) ?? "import:" + Hash(sourcePath)[7..];
        var unitPath = Text(metadata["unit"]?["path"]) ?? sourcePath;
        var runId = Text(metadata["reviewer"]?["runId"]) ?? "import:" + Hash(sourcePath)[7..];
        var reviewedAt = DateTimeOffset.TryParse(Text(metadata["reviewedAt"]), out var parsedAt)
            ? parsedAt.ToUniversalTime() : DateTimeOffset.UnixEpoch;
        var subjectHash = PrefixHash(Text(metadata["reviewedHash"]?["value"]) ?? Hash(sourcePath));
        var inputHash = PrefixHash(Text(metadata["reviewInputs"]?["effectiveHash"]?["value"]) ?? Hash(sourcePath + ":inputs"));
        var promptHash = PrefixHash(Text(metadata["reviewInputs"]?["prompt"]?["contentHash"]) ?? Hash(sourcePath + ":prompt"));
        var model = usage?.EffectiveModel ?? usage?.Model ?? Text(metadata["reviewer"]?["model"]) ?? "unknown";
        var agent = Text(metadata["reviewer"]?["agent"]) ?? usage?.CliType ?? "unknown";
        var grade = metadata["grade"] as JsonObject;
        var aspects = (metadata["aspects"] as JsonArray)?.OfType<JsonObject>().Select(aspect =>
                new QualityObservationAspect(
                    CanonicalAspectId(kind, Text(aspect["id"]) ?? "unknown"),
                    "inconclusive",
                    Text(aspect["rationale"]) ?? "Imported legacy aspect.",
                    Grade: aspect["score"]?.GetValue<int>() is { } aspectScore
                        ? new QualityObservationGrade(aspectScore, Text(aspect["band"]) ?? "unknown") : null))
            .ToArray() ?? [];
        if (aspects.Length == 0 && grade?["score"]?.GetValue<int>() is { } score)
        {
            aspects = [new QualityObservationAspect(
                CanonicalAspectId(kind, kind),
                score >= 80 ? "pass" : score >= 60 ? "concern" : "fail",
                Text(grade["rationale"]) ?? "Imported legacy grade.",
                Grade: new QualityObservationGrade(score, Text(grade["band"]) ?? "unknown"))];
        }
        var assessment = aspects.Any(item => item.Assessment == "fail") ? "fail"
            : aspects.Any(item => item.Assessment == "concern") ? "concern"
            : aspects.Any(item => item.Assessment == "pass") ? "pass" : "inconclusive";
        var completeness = metadata["reviewInputs"] is null || metadata["reviewedHash"] is null
            ? "partial" : "complete";
        var evidence = new List<QualityEvidence>
        {
            new("legacy-sidecar", "document",
                new QualityEvidenceLocator(Path: unitPath, ArtifactRef: sourcePath),
                Text(metadata["summary"]) ?? "Imported review metadata."),
        };
        var findings = ReviewMetaFindings(metadata, sourcePath, kind, unitPath, evidence);
        return Document(
            Id("review-meta", sourcePath, runId, subjectHash, inputHash),
            reviewedAt,
            new QualityObservationSubject(unitId, subjectHash, level),
            new QualityObservationProfile(
                Text(metadata["reviewInputs"]?["prompt"]?["id"]) ?? $"{level}-{kind}-review",
                Text(metadata["reviewInputs"]?["prompt"]?["version"]) ?? "1.0.0",
                promptHash,
                inputHash),
            new QualityObservationProducer(
                "imported", agent, usage?.Provider ?? "unknown",
                usage?.RequestedModel ?? model, model, usage?.ThinkingLevel ?? "unknown",
                usage?.RoutePolicyVersion ?? "unknown", runId, usage?.ReviewRunId ?? runId),
            completeness == "complete" ? "available" : "partial",
            evidence,
            aspects,
            assessment,
            findings,
            new QualityObservationLegacy(
                "review-meta.v" + (metadata["schemaVersion"]?.GetValue<int>() ?? 1),
                sourcePath,
                completeness,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = kind,
                    ["model"] = model,
                }),
            extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [QualityObservationReducer.ProjectionExtension] = JsonSerializer.SerializeToElement(metadata),
                ["quality-studio:legacy-review-meta"] = JsonSerializer.SerializeToElement(metadata),
            });
    }

    private static QualityObservationDocument Document(
        string observationId,
        DateTimeOffset observedAt,
        QualityObservationSubject subject,
        QualityObservationProfile profile,
        QualityObservationProducer producer,
        string evidenceStatus,
        IReadOnlyList<QualityEvidence> evidence,
        IReadOnlyList<QualityObservationAspect> aspects,
        string assessment,
        IReadOnlyList<QualityObservationFinding> findings,
        QualityObservationLegacy legacy,
        string? change = null,
        IReadOnlyDictionary<string, JsonElement>? extensions = null) => new()
    {
        ObservationId = observationId,
        ObservedAt = observedAt.ToUniversalTime(),
        Taxonomy = QualityTaxonomyCatalogue.CoreReference,
        Subject = subject,
        Profile = profile,
        Producer = producer,
        EvidenceStatus = evidenceStatus,
        Evidence = evidence,
        Aspects = aspects,
        Assessment = assessment,
        Change = change,
        Findings = findings,
        Legacy = legacy,
        Extensions = extensions ?? NoExtensions,
    };

    private static IReadOnlyList<QualityObservationFinding> ReviewMetaFindings(
        JsonObject metadata,
        string sourcePath,
        string kind,
        string unitPath,
        List<QualityEvidence> evidence)
    {
        var result = new List<QualityObservationFinding>();
        foreach (var finding in (metadata["findings"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var findingId = Text(finding["id"]) ?? "finding-" + Hash(finding.ToJsonString())[7..];
            var fingerprint = Text(finding["fingerprint"]) ?? Hash($"{sourcePath}\0{findingId}");
            var occurrence = FindingIdentity.OccurrenceFingerprint(fingerprint);
            var references = new List<string>();
            var locations = (finding["locations"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
            for (var index = 0; index < locations.Length; index++)
            {
                var location = locations[index];
                var start = location["range"]?["start"];
                var evidenceId = $"{findingId}-location-{index + 1}";
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    "source-code",
                    new QualityEvidenceLocator(
                        Text(location["path"]) ?? unitPath,
                        Text(location["symbolId"]),
                        start?["line"]?.GetValue<int>(),
                        start?["column"]?.GetValue<int>()),
                    Text(finding["description"]) ?? Text(finding["title"]) ?? "Imported finding location."));
                references.Add(evidenceId);
            }
            if (finding["evidence"] is JsonValue evidenceValue &&
                evidenceValue.TryGetValue<string>(out var legacyEvidence) &&
                !string.IsNullOrWhiteSpace(legacyEvidence))
            {
                var evidenceId = $"{findingId}-legacy-evidence";
                evidence.Add(QualityLegacyMapper.MapEvidence(
                    evidenceId,
                    legacyEvidence,
                    new QualityEvidenceLocator(Path: Text(locations.FirstOrDefault()?["path"]) ?? unitPath)));
                references.Add(evidenceId);
            }
            var source = finding["source"] as JsonObject;
            var deterministic = string.Equals(Text(source?["kind"]), "deterministic", StringComparison.Ordinal);
            var producerKind = deterministic ? "deterministic-sensor" : "unknown";
            var producer = deterministic
                ? Text(source?["sensorId"]) ?? Text(source?["producer"]) ?? "unknown"
                : "unknown";
            result.Add(new QualityObservationFinding(
                findingId,
                FindingIdentity.IssueId(occurrence),
                occurrence,
                FindingIdentity.OccurrenceCanonicalization,
                Text(finding["ruleId"]) ?? "unknown",
                CanonicalAspectId(kind, Text(finding["aspect"]) ?? kind),
                Text(finding["severity"]) ?? "info",
                references,
                new QualityFindingSource(producerKind, producer),
                [fingerprint]));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> DomainExtension<T>(string domain, T value) =>
        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [$"quality-studio:{domain}"] = JsonSerializer.SerializeToElement(value),
        };

    private static string Id(params string[] parts) =>
        "observation-" + Hash("quality-domain-adapter-v1\0" + string.Join('\0', parts))[7..];

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string PrefixHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value : "sha256:" + value;

    private static string? Text(JsonNode? node) => node is JsonValue value &&
        value.TryGetValue<string>(out var text) ? text : null;

    private static string ProducerKind(AttackCoverageSource source) => source switch
    {
        AttackCoverageSource.Agent => "agent",
        AttackCoverageSource.DeterministicSensor => "deterministic-sensor",
        AttackCoverageSource.Human => "human",
        _ => "unknown",
    };

    private static string EvidenceKind(string kind) => kind.ToLowerInvariant() switch
    {
        "source-code" or "source" => "source-code",
        "test" or "test-result" => "test-result",
        "human" or "human-attestation" => "human-attestation",
        "document" => "document",
        _ => "artifact",
    };

    private static string CanonicalAspectId(string kind, string aspect)
    {
        var candidates = new[] { aspect, $"{kind}.{aspect}" };
        var match = QualityTaxonomyCatalogue.CoreDocument.Aspects.FirstOrDefault(term =>
            candidates.Contains(term.Id, StringComparer.Ordinal) ||
            (term.Aliases ?? []).Any(alias => candidates.Contains(alias, StringComparer.Ordinal)));
        return match?.Id ?? $"{kind}.{aspect}";
    }

    private static string Kebab(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
