using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed class QualityTaxonomyOptions
{
    public const string SectionName = "QualityTaxonomy";

    public bool ObservationWriteEnabled { get; set; }
    public bool ObservationReadEnabled { get; set; }
}

public enum QualityObservationAppendResult
{
    Appended,
    AlreadyExists,
}

/// <summary>Append-only repository store for immutable, independently queryable quality observations.</summary>
public static class QualityObservationStore
{
    public const string RelativePath = ".quality/observations";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), RelativePath.Replace('/', Path.DirectorySeparatorChar),
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<QualityObservationAppendResult> AppendAsync(
        string repositoryRoot,
        QualityObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var json = JsonNode.Parse(QualityObservationJson.Serialize(observation))!.ToJsonString();
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && await ContainsAsync(path, observation.ObservationId, cancellationToken)
                    .ConfigureAwait(false))
                return QualityObservationAppendResult.AlreadyExists;

            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n')
                {
                    stream.Position = stream.Length;
                    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }
            stream.Position = stream.Length;
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return QualityObservationAppendResult.Appended;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationReadResult>> ReadAllAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return [];
        var observations = new List<QualityObservationReadResult>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    observations.Add(QualityObservationJson.Read(line));
                }
                catch (JsonException)
                {
                    // A malformed historical line must not hide later immutable observations.
                }
            }
        }
        return observations;
    }

    private static async Task<bool> ContainsAsync(
        string path,
        string observationId,
        CancellationToken cancellationToken)
    {
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("observationId", out var id) &&
                    string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Preserve and tolerate malformed lines; they cannot claim an observation id.
            }
        }
        return false;
    }
}

internal static class ReviewQualityObservationBuilder
{
    public static QualityObservation Build(
        JsonObject metadata,
        ReviewRequest request,
        ReviewUsageEntry usage,
        ReviewAgentResult agentResult,
        ResolvedInputs inputs,
        string relativeSidecarPath,
        string? agentRequestedModel)
    {
        var unit = metadata["unit"]!.AsObject();
        var reviewInputs = metadata["reviewInputs"]!.AsObject();
        var prompt = reviewInputs["prompt"]!.AsObject();
        var manifestHash = Hash(metadata["reviewedHash"]!["value"]!.GetValue<string>());
        var reviewInputsHash = Hash(reviewInputs["effectiveHash"]!["value"]!.GetValue<string>());
        var taxonomy = QualityTaxonomy.CoreReference;
        var observationId = QualityObservationJson.CreateObservationId(
            agentResult.RunId,
            unit["id"]!.GetValue<string>(),
            request.Kind,
            manifestHash,
            reviewInputsHash,
            taxonomy.Digest);
        var evidence = new List<QualityEvidence>();
        var findings = BuildFindings(metadata, evidence);
        var securityProjection = metadata["security"]?["verdict"]?.GetValue<string>() is { } securityVerdict
            ? LegacyQualityMapping.SecurityVerdict(securityVerdict)
            : null;
        var aspects = metadata["aspects"]!.AsArray().OfType<JsonObject>()
            .Where(aspect => !string.Equals(aspect["id"]?.GetValue<string>(), "sensor-availability", StringComparison.Ordinal))
            .Select(BuildAspect)
            .ToArray();
        var requestedModel = First(request.RequestedModel, agentRequestedModel) ?? "unknown";
        var effectiveModel = First(agentResult.EffectiveModel) ?? "unknown";
        var provider = First(agentResult.Provider, request.Provider) ?? "unknown";
        var thinkingLevel = First(agentResult.ThinkingLevel, request.ThinkingLevel) ?? "unknown";
        var routePolicyVersion = First(agentResult.RoutePolicyVersion, request.RoutePolicyVersion) ?? "unknown";
        var unknownRoute = new[] { requestedModel, effectiveModel, provider, thinkingLevel, routePolicyVersion }
            .Any(value => string.Equals(value, "unknown", StringComparison.Ordinal));
        var observedAt = metadata["reviewedAt"]?.GetValue<string>() is { } reviewedAt
            ? DateTimeOffset.Parse(reviewedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : usage.Timestamp;
        var extensions = new Dictionary<string, JsonElement>
        {
            ["compatibilitySidecar"] = JsonSerializer.SerializeToElement(new
            {
                path = relativeSidecarPath,
                schema = metadata["$schema"]?.GetValue<string>(),
            }),
        };

        return new QualityObservation
        {
            ObservationId = observationId,
            ObservedAt = observedAt,
            Taxonomy = taxonomy,
            Subject = new QualitySubject(
                unit["id"]!.GetValue<string>(),
                manifestHash,
                unit["level"]?.GetValue<string>(),
                unit["path"]?.GetValue<string>()),
            Profile = new QualityProfile(
                prompt["id"]!.GetValue<string>(),
                prompt["version"]!.GetValue<string>(),
                Hash(prompt["contentHash"]!.GetValue<string>()),
                reviewInputsHash,
                request.Kind),
            Producer = new QualityProducer(
                QualityTerms.ProducerKind.Agent,
                usage.CliType,
                provider,
                requestedModel,
                effectiveModel,
                thinkingLevel,
                routePolicyVersion,
                agentResult.RunId,
                agentResult.ModelRevision,
                request.ReviewRunId,
                usage.RunId),
            EvidenceStatus = securityProjection?.EvidenceStatus ??
                             (inputs.Complete ? QualityTerms.EvidenceStatus.Available : QualityTerms.EvidenceStatus.Partial),
            Evidence = evidence,
            Aspects = aspects,
            Assessment = securityProjection?.Assessment ?? QualityTerms.Assessment.Inconclusive,
            Decision = securityProjection?.Decision is null
                ? null
                : new QualityDecision(securityProjection.Decision, securityProjection.PolicyRef!),
            Findings = findings,
            Completeness = inputs.Complete && !unknownRoute ? "complete" : "partial",
            Extensions = extensions,
        };
    }

    private static QualityAspectAssessment BuildAspect(JsonObject aspect)
    {
        var legacyId = aspect["id"]!.GetValue<string>();
        var grade = aspect["grade"]!.AsObject();
        return new QualityAspectAssessment(
            LegacyQualityMapping.Aspect(legacyId) ?? legacyId,
            QualityTerms.Assessment.Inconclusive,
            Rationale: grade["rationale"]?.GetValue<string>(),
            Grade: new QualityObservationGrade(
                grade["score"]!.GetValue<int>(),
                grade["band"]!.GetValue<string>()));
    }

    private static IReadOnlyList<QualityObservationFinding> BuildFindings(
        JsonObject metadata,
        List<QualityEvidence> evidence)
    {
        var result = new List<QualityObservationFinding>();
        var index = 0;
        foreach (var finding in metadata["findings"]!.AsArray().OfType<JsonObject>())
        {
            var evidenceRefs = new List<string>();
            var locationIndex = 0;
            foreach (var location in finding["locations"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var evidenceId = $"ev-{index + 1}-location-{++locationIndex}";
                var start = location["range"]?["start"];
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    QualityTerms.EvidenceKind.SourceCode,
                    finding["title"]?.GetValue<string>() ?? "Finding source location.",
                    new QualityEvidenceLocator(
                        location["path"]?.GetValue<string>(),
                        location["symbolId"]?.GetValue<string>(),
                        start?["line"]?.GetValue<int>(),
                        start?["column"]?.GetValue<int>())));
                evidenceRefs.Add(evidenceId);
            }
            if (finding["evidence"]?.GetValue<string>() is { } legacyEvidence)
            {
                var evidenceId = $"ev-{index + 1}-legacy";
                evidence.Add(LegacyQualityMapping.Evidence(evidenceId, legacyEvidence));
                evidenceRefs.Add(evidenceId);
            }

            var fingerprint = finding["fingerprint"]!.GetValue<string>();
            var sourceKind = SourceKind(finding);
            var issueId = "issue-sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes("quality-studio-legacy-issue-v1\0" + fingerprint)));
            result.Add(new QualityObservationFinding(
                finding["id"]?.GetValue<string>() ?? $"finding-{index + 1}",
                issueId,
                fingerprint,
                "quality-studio-occurrence-v1",
                finding["ruleId"]?.GetValue<string>() ?? "unknown",
                LegacyQualityMapping.Aspect(finding["aspect"]?.GetValue<string>() ?? string.Empty) ??
                (finding["aspect"]?.GetValue<string>() ?? "unknown"),
                finding["severity"]?.GetValue<string>() ?? "info",
                evidenceRefs,
                new QualityFindingSource(sourceKind, "self"),
                FingerprintAliases: [fingerprint],
                Title: finding["title"]?.GetValue<string>(),
                Description: finding["description"]?.GetValue<string>(),
                Recommendation: finding["recommendation"]?.GetValue<string>()));
            index++;
        }
        return result;
    }

    private static string SourceKind(JsonObject finding)
    {
        if (finding["source"] is JsonObject source &&
            string.Equals(source["kind"]?.GetValue<string>(), "deterministic", StringComparison.Ordinal))
            return QualityTerms.ProducerKind.DeterministicSensor;
        if (finding["evidence"]?.GetValue<string>() is not { } evidence) return QualityTerms.ProducerKind.Agent;
        try
        {
            using var document = JsonDocument.Parse(evidence);
            return document.RootElement.TryGetProperty("source", out var value) &&
                   string.Equals(value.GetString(), "machine-sensor", StringComparison.Ordinal)
                ? QualityTerms.ProducerKind.DeterministicSensor
                : QualityTerms.ProducerKind.Agent;
        }
        catch (JsonException)
        {
            return QualityTerms.ProducerKind.Agent;
        }
    }

    private static string Hash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;

    private static string? First(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
