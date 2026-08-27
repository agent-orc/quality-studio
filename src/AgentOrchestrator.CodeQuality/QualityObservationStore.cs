using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityObservationLedgerReadResult(
    IReadOnlyList<QualityObservationDocument> Observations,
    IReadOnlyList<QualityObservationReadResult> Unsupported,
    int MalformedLines);

public static class QualityObservationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset timestamp) =>
        Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations",
            timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var serialized = QualityObservationJson.SerializeLine(observation);
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(directory, observation.ObservationId, cancellationToken).ConfigureAwait(false))
                return false;

            Directory.CreateDirectory(directory);
            var bytes = Encoding.UTF8.GetBytes(serialized + "\n");
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<QualityObservationLedgerReadResult> ReadAllAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var observations = new List<QualityObservationDocument>();
        var unsupported = new List<QualityObservationReadResult>();
        var malformed = 0;
        var directory = Path.Combine(Path.GetFullPath(repositoryRoot), ".quality", "observations");
        if (!Directory.Exists(directory))
            return new QualityObservationLedgerReadResult(observations, unsupported, malformed);

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
                    else unsupported.Add(result);
                }
                catch (JsonException)
                {
                    malformed++;
                }
            }
        }

        return new QualityObservationLedgerReadResult(observations, unsupported, malformed);
    }

    private static async Task<bool> ContainsAsync(
        string directory,
        string observationId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var parsed = JsonDocument.Parse(line);
                    if (parsed.RootElement.ValueKind == JsonValueKind.Object &&
                        parsed.RootElement.TryGetProperty("observationId", out var id) &&
                        string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                        return true;
                }
                catch (JsonException)
                {
                    // A malformed historical line never hides later valid observations.
                }
            }
        }

        return false;
    }
}

public static class QualityObservationFactory
{
    public static QualityObservationDocument FromReviewMeta(
        JsonObject meta,
        ReviewRequest request,
        ReviewUsageEntry usage,
        IReviewAgent agent)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(agent);

        var unit = meta["unit"]!.AsObject();
        var reviewInputs = meta["reviewInputs"]!.AsObject();
        var prompt = reviewInputs["prompt"]!.AsObject();
        var reviewedHash = Digest(meta["reviewedHash"]!["value"]!.GetValue<string>());
        var reviewInputsHash = Digest(reviewInputs["effectiveHash"]!["value"]!.GetValue<string>());
        var promptHash = Digest(prompt["contentHash"]!.GetValue<string>());
        var runId = meta["reviewer"]!["runId"]!.GetValue<string>();
        var unitId = unit["id"]!.GetValue<string>();
        var kind = meta["kind"]!.GetValue<string>();
        var observedAt = DateTimeOffset.Parse(meta["reviewedAt"]!.GetValue<string>(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal).ToUniversalTime();
        var evidence = new List<QualityEvidence>();
        var findings = CreateFindings(meta, evidence);
        var aspects = meta["aspects"]!.AsArray().OfType<JsonObject>().Select(aspect =>
        {
            var legacyId = aspect["id"]!.GetValue<string>();
            var aspectId = LegacyQualityTaxonomyMapper.MapAspect(legacyId) ?? ExtensionAspect(legacyId);
            var grade = aspect["grade"]!.AsObject();
            var extensions = aspectId == legacyId
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["quality.studio:legacy-aspect-id"] = JsonSerializer.SerializeToElement(legacyId),
                };
            return new QualityAspectAssessment(
                aspectId,
                "assessment",
                "not-assessed",
                "The legacy review contract records a grade but no assessment axis.",
                new QualityObservationGrade(
                    grade["score"]!.GetValue<int>(),
                    grade["band"]!.GetValue<string>(),
                    grade["rationale"]?.GetValue<string>()),
                extensions);
        }).ToArray();
        var taxonomyDigest = QualityTaxonomyCatalogueStore.CoreDigest;
        var observationId = CreateId(runId, unitId, kind, reviewedHash, reviewInputsHash, taxonomyDigest);
        return new QualityObservationDocument
        {
            ObservationId = observationId,
            ObservedAt = observedAt,
            Taxonomy = new QualityTaxonomyReference(
                QualityTaxonomyTerms.CoreId, QualityTaxonomyTerms.CoreVersion, taxonomyDigest),
            Subject = new QualityObservationSubject(
                unitId,
                reviewedHash,
                unit["level"]?.GetValue<string>() ?? "unknown",
                unit["path"]?.GetValue<string>()),
            Profile = new QualityReviewProfile(
                prompt["id"]!.GetValue<string>(),
                prompt["version"]!.GetValue<string>(),
                promptHash,
                reviewInputsHash,
                kind),
            Producer = new QualityProducer(
                "agent",
                agent.AgentName,
                Text(usage.Provider) ?? "unknown",
                Text(usage.RequestedModel) ?? "unknown",
                Text(usage.EffectiveModel) ?? Text(usage.Model) ?? "unknown",
                ThinkingLevel: Text(usage.ThinkingLevel) ?? "unknown",
                RoutePolicyVersion: Text(usage.RoutePolicyVersion) ?? "unknown",
                RunId: runId,
                ReviewRunId: Text(request.ReviewRunId)),
            EvidenceStatus = reviewInputs["complete"]!.GetValue<bool>() ? "available" : "partial",
            Evidence = evidence,
            Aspects = aspects,
            Assessment = "not-assessed",
            Findings = findings,
        };
    }

    public static string CreateId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string inputHash,
        string taxonomyDigest)
    {
        var canonical = string.Join('\0',
            "quality-studio-observation-id-v1", runId, unitId, kind, subjectHash, inputHash, taxonomyDigest);
        return "observation-sha256:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static IReadOnlyList<QualityObservationFinding> CreateFindings(
        JsonObject meta,
        ICollection<QualityEvidence> evidence)
    {
        var result = new List<QualityObservationFinding>();
        foreach (var finding in meta["findings"]!.AsArray().OfType<JsonObject>())
        {
            var findingId = finding["id"]!.GetValue<string>();
            var refs = new List<string>();
            string? legacyEvidenceText = null;
            var locationIndex = 0;
            foreach (var location in finding["locations"]!.AsArray().OfType<JsonObject>())
            {
                var evidenceId = $"ev-{findingId}-location-{++locationIndex}";
                var range = location["range"] as JsonObject;
                var start = range?["start"] as JsonObject;
                var end = range?["end"] as JsonObject;
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    "source-code",
                    finding["title"]?.GetValue<string>() ?? "Finding location.",
                    new QualityEvidenceLocator(
                        location["path"]?.GetValue<string>(),
                        location["symbolId"]?.GetValue<string>(),
                        StartLine: start?["line"]?.GetValue<int>(),
                        StartColumn: start?["column"]?.GetValue<int>(),
                        EndLine: end?["line"]?.GetValue<int>(),
                        EndColumn: end?["column"]?.GetValue<int>())));
                refs.Add(evidenceId);
            }
            if (finding["evidence"] is JsonValue evidenceValue &&
                evidenceValue.TryGetValue<string>(out var legacyEvidence))
            {
                legacyEvidenceText = legacyEvidence;
                var evidenceId = $"ev-{findingId}-legacy";
                evidence.Add(LegacyQualityTaxonomyMapper.MapEvidence(evidenceId, legacyEvidence));
                refs.Add(evidenceId);
            }

            var legacyAspect = finding["aspect"]!.GetValue<string>();
            var source = finding["source"] as JsonObject;
            var evidenceProvesSensor = TryReadSensorId(legacyEvidenceText, out var evidenceSensorId);
            var sourceKind = string.Equals(source?["kind"]?.GetValue<string>(), "deterministic",
                StringComparison.Ordinal) || evidenceProvesSensor ? "deterministic-sensor" : "agent";
            result.Add(new QualityObservationFinding(
                findingId,
                finding["fingerprint"]!.GetValue<string>(),
                "quality-studio-occurrence-v1",
                finding["ruleId"]!.GetValue<string>(),
                LegacyQualityTaxonomyMapper.MapAspect(legacyAspect, finding["ruleId"]?.GetValue<string>()) ??
                ExtensionAspect(legacyAspect),
                finding["severity"]!.GetValue<string>(),
                refs,
                new QualityFindingSource(sourceKind, sourceKind == "agent" ? "self" :
                    source?["sensorId"]?.GetValue<string>() ?? evidenceSensorId ?? "unknown"),
                FingerprintAliases: [finding["fingerprint"]!.GetValue<string>()],
                Title: finding["title"]?.GetValue<string>(),
                Description: finding["description"]?.GetValue<string>(),
                Recommendation: finding["recommendation"]?.GetValue<string>()));
        }

        return result;
    }

    private static bool TryReadSensorId(string? evidence, out string? sensorId)
    {
        sensorId = null;
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        try
        {
            using var parsed = JsonDocument.Parse(evidence);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("source", out var source) ||
                !string.Equals(source.GetString(), "machine-sensor", StringComparison.Ordinal) ||
                !root.TryGetProperty("sensorId", out var id) ||
                string.IsNullOrWhiteSpace(id.GetString()))
                return false;
            sensorId = id.GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Digest(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;

    private static string ExtensionAspect(string legacyId)
    {
        var normalized = new string(legacyId.ToLowerInvariant().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '-').ToArray()).Trim('-');
        return "quality.studio:legacy." + (string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized);
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class QualityTaxonomyConfiguration
{
    public const string ObservationWriteEnabledKey = "QualityTaxonomy:ObservationWriteEnabled";

    public static bool ObservationWriteEnabled(bool? requestOverride)
    {
        if (requestOverride.HasValue) return requestOverride.Value;
        var value = AppContext.GetData(ObservationWriteEnabledKey)?.ToString()
                    ?? Environment.GetEnvironmentVariable("QUALITY_TAXONOMY__OBSERVATION_WRITE_ENABLED")
                    ?? Environment.GetEnvironmentVariable(ObservationWriteEnabledKey);
        return bool.TryParse(value, out var enabled) && enabled;
    }
}
