using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public static class QualityTaxonomySettings
{
    public const string ObservationWriteEnabled = "QualityTaxonomy:ObservationWriteEnabled";
    private const string ObservationWriteEnvironmentName = "QualityTaxonomy__ObservationWriteEnabled";

    public static bool ResolveObservationWriteEnabled(bool? requestValue)
    {
        if (requestValue.HasValue) return requestValue.Value;
        var configured = Environment.GetEnvironmentVariable(ObservationWriteEnvironmentName) ??
                         Environment.GetEnvironmentVariable(ObservationWriteEnabled);
        return bool.TryParse(configured, out var enabled) && enabled;
    }
}

/// <summary>Append-only, repository-local immutable observation ledger.</summary>
public static class QualityObservationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions CompactOptions = new(QualityObservationJson.Options)
    {
        WriteIndented = false,
    };

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        _ = QualityObservationJson.Serialize(observation);
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(path, observation.ObservationId, cancellationToken).ConfigureAwait(false))
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observation, CompactOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationDocument>> ReadAllAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var observations = new List<QualityObservationDocument>();
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory)) return observations;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var result = QualityObservationJson.Read(line);
                    if (result.Observation is not null) observations.Add(result.Observation);
                }
                catch (JsonException)
                {
                    // One malformed or unsupported historical line must not hide later immutable observations.
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
        if (!File.Exists(path)) return false;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var parsed = JsonDocument.Parse(line);
                if (parsed.RootElement.TryGetProperty("observationId", out var id) &&
                    string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                    return true;
            }
            catch (JsonException)
            {
                // Preserve malformed lines and continue looking for an exact idempotency key.
            }
        }
        return false;
    }
}

public static class ReviewQualityObservationFactory
{
    public const string ProfileVersion = "1.0.0";
    public const string FingerprintAlgorithm = "quality-studio-occurrence-v1";

    public static QualityObservationDocument Create(
        JsonObject reviewMeta,
        string requestedModel,
        string? provider,
        string? thinkingLevel,
        string? routePolicyVersion,
        string? reviewRunId)
    {
        ArgumentNullException.ThrowIfNull(reviewMeta);
        var unit = reviewMeta["unit"]!.AsObject();
        var reviewer = reviewMeta["reviewer"]!.AsObject();
        var reviewedHash = reviewMeta["reviewedHash"]!["value"]!.GetValue<string>();
        var reviewInputs = reviewMeta["reviewInputs"]!.AsObject();
        var prompt = reviewInputs["prompt"]!.AsObject();
        var reviewInputsHash = reviewInputs["effectiveHash"]!["value"]!.GetValue<string>();
        var runId = reviewer["runId"]!.GetValue<string>();
        var kind = reviewMeta["kind"]!.GetValue<string>();
        var taxonomyDigest = QualityTaxonomyCatalogueLoader.CoreDigest;
        var observationId = ComputeObservationId(
            runId,
            unit["id"]!.GetValue<string>(),
            kind,
            reviewedHash,
            reviewInputsHash,
            taxonomyDigest);
        var aspects = reviewMeta["aspects"]!.AsArray().OfType<JsonObject>()
            .Select(MapAspect).ToArray();
        var findings = reviewMeta["findings"]!.AsArray().OfType<JsonObject>()
            .Select(MapFinding).ToArray();
        return new QualityObservationDocument
        {
            ObservationId = observationId,
            ObservedAt = DateTimeOffset.Parse(reviewMeta["reviewedAt"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            Taxonomy = new QualityTaxonomyReference
            {
                Id = QualityTaxonomyConstants.CoreCatalogueId,
                Version = QualityTaxonomyConstants.CoreCatalogueVersion,
                Digest = taxonomyDigest,
            },
            Subject = new QualityObservationSubject
            {
                UnitId = unit["id"]!.GetValue<string>(),
                ManifestHash = WithSha256Prefix(reviewedHash),
            },
            Profile = new QualityObservationProfile
            {
                Id = prompt["id"]!.GetValue<string>(),
                Version = prompt["version"]!.GetValue<string>(),
                PromptHash = WithSha256Prefix(prompt["contentHash"]!.GetValue<string>()),
                ReviewInputsHash = WithSha256Prefix(reviewInputsHash),
            },
            Producer = new QualityObservationProducer
            {
                Kind = QualityProducerKind.Agent,
                Agent = reviewer["agent"]!.GetValue<string>(),
                Provider = Unknown(provider),
                RequestedModel = Unknown(requestedModel),
                EffectiveModel = Unknown(reviewer["model"]!.GetValue<string>()),
                ThinkingLevel = Unknown(thinkingLevel),
                RoutePolicyVersion = Unknown(routePolicyVersion),
                RunId = runId,
                ReviewRunId = reviewRunId,
            },
            EvidenceStatus = reviewInputs["complete"]!.GetValue<bool>()
                ? QualityEvidenceStatus.Available
                : QualityEvidenceStatus.Partial,
            Evidence = [],
            Aspects = aspects,
            Assessment = AssessmentFromGrade(reviewMeta["grade"]!["band"]!.GetValue<string>()),
            Findings = findings,
        };
    }

    public static string ComputeObservationId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string reviewInputsHash,
        string taxonomyDigest)
    {
        var canonical = string.Join('\0',
            "quality-studio-observation-id-v1", runId, unitId, kind, subjectHash, reviewInputsHash, taxonomyDigest);
        return "observation-sha256:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static QualityObservationAspect MapAspect(JsonObject aspect)
    {
        var legacyId = aspect["id"]!.GetValue<string>();
        return new QualityObservationAspect
        {
            AspectId = MapAspectId(legacyId),
            Assessment = AssessmentFromGrade(aspect["grade"]!["band"]!.GetValue<string>()),
            Rationale = aspect["grade"]!["rationale"]!.GetValue<string>(),
            Grade = new QualityObservationGrade
            {
                Score = aspect["grade"]!["score"]!.GetValue<int>(),
                Band = aspect["grade"]!["band"]!.GetValue<string>(),
            },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["quality-studio/legacy-review-meta"] = JsonSerializer.SerializeToElement(new { aspectId = legacyId }),
            },
        };
    }

    private static QualityObservationFinding MapFinding(JsonObject finding)
    {
        var fingerprint = WithSha256Prefix(finding["fingerprint"]!.GetValue<string>());
        var source = finding["source"]?.AsObject();
        var sourceKind = source?["kind"]?.GetValue<string>() == "deterministic"
            ? QualityProducerKind.DeterministicSensor
            : QualityProducerKind.Unknown;
        var legacyId = finding["id"]!.GetValue<string>();
        return new QualityObservationFinding
        {
            ObservationFindingId = "of-" + SafeId(legacyId),
            IssueId = "issue-" + fingerprint["sha256:".Length..],
            OccurrenceFingerprint = fingerprint,
            FingerprintAlgorithm = FingerprintAlgorithm,
            FingerprintAliases = [fingerprint],
            RuleRef = finding["ruleId"]!.GetValue<string>(),
            AspectId = MapAspectId(finding["aspect"]!.GetValue<string>()),
            Severity = Enum.Parse<FindingSeverity>(finding["severity"]!.GetValue<string>(), true),
            EvidenceRefs = [],
            Source = new QualityFindingSource
            {
                Kind = sourceKind,
                ProducerRef = sourceKind == QualityProducerKind.DeterministicSensor
                    ? source?["sensorId"]?.GetValue<string>() ?? "unknown"
                    : "unknown",
            },
            Extensions = new Dictionary<string, JsonElement>
            {
                ["quality-studio/legacy-review-meta"] = JsonSerializer.SerializeToElement(new { findingId = legacyId }),
            },
        };
    }

    private static QualityAssessment AssessmentFromGrade(string band) => band switch
    {
        "A" or "B" => QualityAssessment.Pass,
        "C" => QualityAssessment.Concern,
        "D" or "F" => QualityAssessment.Fail,
        _ => QualityAssessment.Inconclusive,
    };

    private static string MapAspectId(string legacyId)
    {
        try
        {
            return LegacyQualityTaxonomyMapper.MapAspect(legacyId) ?? "quality-studio.legacy:sensor-availability";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "quality-studio.legacy:" + SafeId(legacyId);
        }
    }

    private static string SafeId(string value)
    {
        var safe = new string(value.ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string Unknown(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    private static string WithSha256Prefix(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;
}
