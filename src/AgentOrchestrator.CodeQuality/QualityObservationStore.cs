using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public interface IQualityObservationWriter
{
    Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only, repository-local quality history. Duplicate observation ids are replay-safe,
/// malformed historical lines do not hide later valid records, and unsupported majors retain raw JSON.
/// </summary>
public sealed class QualityObservationLedger : IQualityObservationWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset observedAt) =>
        Path.Combine(
            Path.GetFullPath(repositoryRoot),
            ".quality",
            "observations",
            observedAt.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(path, observation.ObservationId, cancellationToken).ConfigureAwait(false))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = QualityObservationJson.Serialize(observation)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal) + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<QualityObservationReadResult>> ReadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return [];
        var results = new List<QualityObservationReadResult>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    results.Add(QualityObservationJson.Read(line));
                }
                catch (JsonException)
                {
                    // A partial/corrupt historical line must not hide later immutable observations.
                }
            }
        }
        return results;
    }

    private static async Task<bool> ContainsAsync(
        string path,
        string observationId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("observationId", out var id) &&
                    string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Ignore an incomplete line while checking idempotency; append remains recoverable.
            }
        }
        return false;
    }
}

public static class ReviewQualityObservationFactory
{
    public const string OccurrenceFingerprintAlgorithm = "quality-studio-occurrence-v2";

    public static QualityObservationDocument Create(
        JsonObject metadata,
        ReviewRequest request,
        ReviewUsageEntry usage,
        IReadOnlySet<string> agentFindingIds)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(agentFindingIds);
        var catalogue = QualityTaxonomyCatalogue.LoadCore();
        var unit = metadata["unit"]!.AsObject();
        var reviewInputs = metadata["reviewInputs"]!.AsObject();
        var reviewedHash = Hash(metadata["reviewedHash"]!["value"]!.GetValue<string>());
        var reviewInputsHash = Hash(reviewInputs["effectiveHash"]!["value"]!.GetValue<string>());
        var prompt = reviewInputs["prompt"]!.AsObject();
        var evidence = new List<QualityEvidence>();
        var findings = new List<QualityObservationFinding>();
        var evidenceIndex = 0;

        foreach (var findingNode in metadata["findings"]!.AsArray().OfType<JsonObject>())
        {
            var evidenceRefs = new List<string>();
            foreach (var location in findingNode["locations"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var evidenceId = $"ev-{++evidenceIndex}";
                var range = location["range"]?.AsObject();
                var start = range?["start"]?.AsObject();
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    QualityEvidenceKind.SourceCode,
                    new QualityEvidenceLocator(
                        location["path"]?.GetValue<string>(),
                        location["symbolId"]?.GetValue<string>(),
                        Line: start?["line"]?.GetValue<int>(),
                        Column: start?["column"]?.GetValue<int>()),
                    findingNode["description"]?.GetValue<string>() ?? "Preserved finding location."));
                evidenceRefs.Add(evidenceId);
            }
            if (findingNode["evidence"] is JsonValue evidenceValue &&
                evidenceValue.TryGetValue<string>(out var legacyEvidence))
            {
                var evidenceId = $"ev-{++evidenceIndex}";
                evidence.Add(LegacyQualityMapper.MapEvidenceString(legacyEvidence, evidenceId));
                evidenceRefs.Add(evidenceId);
            }
            if (evidenceRefs.Count == 0)
            {
                var evidenceId = $"ev-{++evidenceIndex}";
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    QualityEvidenceKind.Document,
                    new QualityEvidenceLocator(),
                    findingNode["description"]?.GetValue<string>() ?? "Preserved legacy finding evidence."));
                evidenceRefs.Add(evidenceId);
            }

            var findingId = findingNode["id"]!.GetValue<string>();
            var fingerprint = findingNode["fingerprint"]!.GetValue<string>();
            var aspectId = catalogue.ResolveCanonicalId("aspect", findingNode["aspect"]!.GetValue<string>());
            var producerKind = agentFindingIds.Contains(findingId)
                ? QualityProducerKind.Agent
                : QualityProducerKind.DeterministicSensor;
            findings.Add(new QualityObservationFinding(
                findingId,
                FindingLifecycleStore.IssueId(fingerprint),
                fingerprint,
                OccurrenceFingerprintAlgorithm,
                findingNode["ruleId"]!.GetValue<string>(),
                aspectId,
                Enum.Parse<FindingSeverity>(findingNode["severity"]!.GetValue<string>(), ignoreCase: true),
                evidenceRefs,
                new QualityFindingSource(producerKind, producerKind == QualityProducerKind.Agent ? "self" : "review-sensor"),
                [fingerprint]));
        }

        var aspects = metadata["aspects"]!.AsArray().OfType<JsonObject>().Select(aspect =>
        {
            var grade = aspect["grade"]!.AsObject();
            var band = Enum.Parse<GradeBand>(grade["band"]!.GetValue<string>());
            return new QualityObservationAspect(
                catalogue.ResolveCanonicalId("aspect", aspect["id"]!.GetValue<string>()),
                grade["rationale"]!.GetValue<string>(),
                Assessment(band),
                Grade: new QualityObservationGrade(grade["score"]!.GetValue<int>(), band));
        }).ToArray();
        var overallGrade = metadata["grade"]!.AsObject();
        var overallBand = Enum.Parse<GradeBand>(overallGrade["band"]!.GetValue<string>());
        var observedAt = DateTimeOffset.Parse(metadata["reviewedAt"]!.GetValue<string>()).ToUniversalTime();
        var decision = SecurityDecision(metadata);
        var observationId = ObservationId(
            usage.RunId,
            unit["id"]!.GetValue<string>(),
            request.Kind,
            reviewedHash,
            reviewInputsHash,
            catalogue.Digest);

        return new QualityObservationDocument
        {
            ObservationId = observationId,
            ObservedAt = observedAt,
            Taxonomy = catalogue.CreateReference(),
            Subject = new QualityObservationSubject(unit["id"]!.GetValue<string>(), reviewedHash),
            Profile = new QualityObservationProfile(
                prompt["id"]!.GetValue<string>(),
                prompt["version"]!.GetValue<string>(),
                prompt["contentHash"]!.GetValue<string>(),
                reviewInputsHash),
            Producer = new QualityObservationProducer(
                QualityProducerKind.Agent,
                usage.CliType,
                usage.Provider ?? "unknown",
                usage.RequestedModel ?? "unknown",
                usage.EffectiveModel ?? usage.Model,
                usage.ThinkingLevel ?? "unknown",
                usage.RoutePolicyVersion ?? "unknown",
                usage.RunId,
                usage.ReviewRunId ?? "unknown"),
            EvidenceStatus = EvidenceStatus(metadata),
            Evidence = evidence,
            Aspects = aspects,
            Assessment = Assessment(overallBand),
            Decision = decision,
            Findings = findings,
        };
    }

    public static string ObservationId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string reviewInputsHash,
        string taxonomyDigest)
    {
        var canonical = string.Join('\0',
            "quality-studio-observation-v1",
            runId,
            unitId,
            kind,
            subjectHash,
            reviewInputsHash,
            taxonomyDigest);
        return "observation-sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static QualityAssessment Assessment(GradeBand band) => band switch
    {
        GradeBand.A or GradeBand.B => QualityAssessment.Pass,
        GradeBand.C => QualityAssessment.Concern,
        GradeBand.D or GradeBand.F => QualityAssessment.Fail,
        _ => throw new ArgumentOutOfRangeException(nameof(band)),
    };

    private static QualityObservationDecision? SecurityDecision(JsonObject metadata)
    {
        if (metadata["security"]?["verdict"]?.GetValue<string>() is not { } verdict) return null;
        var mapped = LegacyQualityMapper.MapSecurityVerdict(verdict);
        return mapped.Decision is { } decision
            ? new QualityObservationDecision(decision, mapped.PolicyRef!)
            : null;
    }

    private static QualityEvidenceStatus EvidenceStatus(JsonObject metadata)
    {
        if (metadata["security"]?["verdict"]?.GetValue<string>() == "unavailable")
            return QualityEvidenceStatus.Unavailable;
        if (metadata["deterministicEvidence"]?.AsArray().OfType<JsonObject>()
            .Any(result => result["available"]?.GetValue<bool>() == false) == true)
            return QualityEvidenceStatus.Partial;
        return QualityEvidenceStatus.Available;
    }

    private static string Hash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;
}
