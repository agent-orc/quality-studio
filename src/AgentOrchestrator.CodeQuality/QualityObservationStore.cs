using System.Collections.Concurrent;
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

    public static QualityTaxonomyOptions FromEnvironment(Func<string, string?>? valueProvider = null)
    {
        valueProvider ??= name => Environment.GetEnvironmentVariable(name);
        return new QualityTaxonomyOptions
        {
            ObservationWriteEnabled = ReadFlag(
                valueProvider,
                $"{SectionName}__{nameof(ObservationWriteEnabled)}",
                "QUALITY_TAXONOMY_OBSERVATION_WRITE_ENABLED"),
            ObservationReadEnabled = ReadFlag(
                valueProvider,
                $"{SectionName}__{nameof(ObservationReadEnabled)}",
                "QUALITY_TAXONOMY_OBSERVATION_READ_ENABLED"),
        };
    }

    private static bool ReadFlag(
        Func<string, string?> valueProvider,
        string configurationName,
        string alias)
    {
        var raw = valueProvider(configurationName) ?? valueProvider(alias);
        return bool.TryParse(raw, out var enabled) && enabled;
    }
}

public sealed record QualityObservationStoreReadResult(
    IReadOnlyList<QualityObservation> Observations,
    IReadOnlyList<QualityObservationReadResult> Unsupported,
    int MalformedLines);

/// <summary>Append-only, idempotent repository store for immutable quality observations.</summary>
public sealed class QualityObservationStore
{
    public const string RelativeDirectory = ".quality/observations";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions CompactOptions = new(QualityObservationJson.Options)
    {
        WriteIndented = false,
    };
    private readonly string directory;

    public QualityObservationStore(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        directory = Path.Combine(Path.GetFullPath(repositoryRoot),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    public string GetLedgerPath(DateTimeOffset timestamp) =>
        Path.Combine(directory, timestamp.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public async Task<bool> AppendAsync(
        QualityObservation observation,
        CancellationToken cancellationToken = default)
    {
        QualityObservationJson.Validate(observation);
        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(observation.ObservationId, cancellationToken).ConfigureAwait(false)) return false;
            Directory.CreateDirectory(directory);
            var path = GetLedgerPath(observation.ObservedAt);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observation, CompactOptions) + "\n");
            await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length > 0)
            {
                stream.Position = stream.Length - 1;
                if (stream.ReadByte() != '\n')
                {
                    stream.Position = stream.Length;
                    stream.WriteByte((byte)'\n');
                }
            }
            stream.Position = stream.Length;
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<QualityObservationStoreReadResult> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var observations = new List<QualityObservation>();
        var unsupported = new List<QualityObservationReadResult>();
        var malformed = 0;
        if (!Directory.Exists(directory)) return new(observations, unsupported, malformed);
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var result = QualityObservationJson.Read(line);
                    if (result.Observation is { } observation) observations.Add(observation);
                    else unsupported.Add(result);
                }
                catch (JsonException)
                {
                    malformed++;
                }
            }
        }
        return new(observations, unsupported, malformed);
    }

    private async Task<bool> ContainsAsync(string observationId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("observationId", out var id) &&
                        string.Equals(id.GetString(), observationId, StringComparison.Ordinal)) return true;
                }
                catch (JsonException)
                {
                    // A partial line does not hide later valid entries and is never rewritten.
                }
            }
        }
        return false;
    }
}

public static class QualityObservationIdentity
{
    public const string FingerprintAlgorithm = "quality-studio-occurrence-v2";

    public static string ObservationId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string inputHash,
        string taxonomyDigest) =>
        "observation-sha256:" + HashBody(
            $"quality-studio/observation/v1\0{runId}\0{unitId}\0{kind}\0{subjectHash}\0{inputHash}\0{taxonomyDigest}");

    public static string OccurrenceFingerprint(
        string path,
        string ruleRef,
        FindingRange? range,
        string? symbolId) =>
        "sha256:" + HashBody(
            $"{FingerprintAlgorithm}\0{Normalize(path)}\0{ruleRef}\0{symbolId ?? string.Empty}\0" +
            $"{range?.Start.Line ?? 0}:{range?.Start.Column ?? 0}-{range?.End.Line ?? 0}:{range?.End.Column ?? 0}");

    public static string IssueId(string path, string ruleRef, string? symbolId) =>
        "issue-sha256:" + HashBody(
            $"quality-studio/issue/v1\0{Normalize(path)}\0{ruleRef}\0{symbolId ?? string.Empty}");

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');
    private static string HashBody(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public static class GeneralReviewObservationAdapter
{
    public static QualityObservation Create(
        JsonObject response,
        string unitId,
        string subjectHash,
        string reviewInputsHash,
        string kind,
        ReviewLevel level,
        string runId,
        string? reviewRunId,
        DateTimeOffset observedAt,
        string agent,
        string? provider,
        string? requestedModel,
        string? effectiveModel,
        string? thinkingLevel,
        string? routePolicyVersion,
        SecurityEvidenceBundle sensorEvidence,
        IReadOnlyList<SensorScanResult> deterministicEvidence)
    {
        ArgumentNullException.ThrowIfNull(response);
        var evidence = new List<QualityEvidence>();
        var findings = new List<QualityObservationFinding>();
        foreach (var finding in response["findings"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var findingId = finding["id"]?.GetValue<string>() ?? $"of-{findings.Count + 1}";
            var legacyAspect = finding["aspect"]?.GetValue<string>() ?? kind;
            var aspectId = MapAspect(legacyAspect, kind);
            var evidenceRefs = new List<string>();
            var locations = finding["locations"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            foreach (var location in locations)
            {
                var id = $"ev-{evidence.Count + 1}";
                evidence.Add(LocationEvidence(id, location, finding["title"]?.GetValue<string>() ?? findingId));
                evidenceRefs.Add(id);
            }
            if (finding["evidence"]?.GetValue<string>() is { } legacyEvidence)
            {
                var id = $"ev-{evidence.Count + 1}";
                evidence.Add(QualityLegacyMappings.MapEvidenceString(id, legacyEvidence));
                evidenceRefs.Add(id);
            }
            var firstLocation = ParseLocation(locations.FirstOrDefault());
            var rule = finding["ruleId"]?.GetValue<string>() ?? findingId;
            var legacyFingerprint = finding["fingerprint"]?.GetValue<string>();
            var source = finding["source"]?.AsObject();
            var sourceKind = string.Equals(source?["kind"]?.GetValue<string>(), "deterministic", StringComparison.Ordinal)
                ? QualityProducerKind.DeterministicSensor
                : QualityProducerKind.Agent;
            var occurrence = QualityObservationIdentity.OccurrenceFingerprint(
                firstLocation.Path ?? ".", rule, firstLocation.Range, firstLocation.SymbolId);
            findings.Add(new QualityObservationFinding(
                findingId,
                QualityObservationIdentity.IssueId(firstLocation.Path ?? ".", rule, firstLocation.SymbolId),
                occurrence,
                QualityObservationIdentity.FingerprintAlgorithm,
                legacyFingerprint is null || legacyFingerprint == occurrence ? [] : [legacyFingerprint],
                rule,
                aspectId,
                ParseSeverity(finding["severity"]?.GetValue<string>()),
                finding["title"]?.GetValue<string>() ?? findingId,
                finding["description"]?.GetValue<string>() ?? "No description was supplied.",
                finding["recommendation"]?.GetValue<string>() ?? "No recommendation was supplied.",
                evidenceRefs,
                new QualityFindingSource(sourceKind, sourceKind == QualityProducerKind.Agent
                    ? "self"
                    : source?["sensorId"]?.GetValue<string>() ?? "unknown"),
                QualityObservationJson.NoExtensions));
        }

        foreach (var sensor in deterministicEvidence)
        {
            foreach (var finding in sensor.Findings)
            {
                var refs = new List<string>();
                foreach (var evidenceLocation in finding.Locations.DefaultIfEmpty())
                {
                    var id = $"ev-{evidence.Count + 1}";
                    evidence.Add(new QualityEvidence(id, QualityEvidenceKind.ToolResult,
                        new QualityEvidenceLocator(evidenceLocation?.Path, evidenceLocation?.SymbolId,
                            evidenceLocation?.Range?.Start.Line, evidenceLocation?.Range?.Start.Column,
                            evidenceLocation?.Range?.End.Line, evidenceLocation?.Range?.End.Column,
                            sensor.Provenance.SensorId),
                        finding.Title, null, "application/json", null, QualityObservationJson.NoExtensions));
                    refs.Add(id);
                }
                if (finding.Evidence is not null)
                {
                    var id = $"ev-{evidence.Count + 1}";
                    evidence.Add(QualityLegacyMappings.MapEvidenceString(id, finding.Evidence));
                    refs.Add(id);
                }
                var location = finding.Locations.FirstOrDefault();
                var occurrence = QualityObservationIdentity.OccurrenceFingerprint(
                    location?.Path ?? ".", finding.RuleId, location?.Range, location?.SymbolId);
                findings.Add(new QualityObservationFinding(
                    finding.Id,
                    QualityObservationIdentity.IssueId(
                        location?.Path ?? ".", finding.RuleId, location?.SymbolId),
                    occurrence,
                    QualityObservationIdentity.FingerprintAlgorithm,
                    finding.Fingerprint == occurrence ? [] : [finding.Fingerprint],
                    finding.RuleId,
                    MapAspect(finding.Aspect, kind),
                    finding.Severity,
                    finding.Title,
                    finding.Description,
                    finding.Recommendation,
                    refs,
                    new QualityFindingSource(
                        QualityProducerKind.DeterministicSensor, sensor.Provenance.SensorId),
                    QualityObservationJson.NoExtensions));
            }
        }

        var aspects = (response["aspects"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(aspect => aspect["id"]?.GetValue<string>() != "sensor-availability")
            .Select(aspect =>
            {
                var grade = ParseGrade(aspect["grade"]?.AsObject());
                return new QualityAspectObservation(
                    MapAspect(aspect["id"]?.GetValue<string>() ?? kind, kind),
                    aspect["title"]?.GetValue<string>(),
                    QualityLegacyMappings.MapGrade(grade.Band),
                    null,
                    aspect["grade"]?["rationale"]?.GetValue<string>() ?? "Imported review grade.",
                    grade,
                    QualityObservationJson.NoExtensions);
            }).ToArray();
        var overallGrade = ParseGrade(response["grade"]?.AsObject());
        var evidenceStatus = deterministicEvidence.Any(item => !item.Available)
            ? deterministicEvidence.Any(item => item.Available) ? QualityEvidenceStatus.Partial : QualityEvidenceStatus.Unavailable
            : QualityEvidenceStatus.Available;
        QualityPolicyDecision? decision = null;
        var assessment = QualityLegacyMappings.MapGrade(overallGrade.Band);
        if (kind == "security" && sensorEvidence.Sensors.Count > 0)
        {
            var mapping = QualityLegacyMappings.MapSecurityVerdict(
                SecurityEvidenceBundle.VerdictName(sensorEvidence.Verdict));
            evidenceStatus = mapping.EvidenceStatus;
            decision = mapping.Decision;
            assessment = MostSevere(assessment, mapping.Assessment);
        }
        var promptHash = ReviewPromptBuilder.TemplateHash(kind);
        var taxonomy = new QualityCatalogueReference(QualityTaxonomyCatalogue.CoreId,
            QualityTaxonomyCatalogue.CoreVersion, QualityTaxonomyCatalogue.CoreDigest);
        var observationId = QualityObservationIdentity.ObservationId(
            runId, unitId, kind, subjectHash, reviewInputsHash, taxonomy.Digest);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["quality-studio/overall-grade"] = JsonSerializer.SerializeToElement(
                overallGrade, QualityObservationJson.Options),
        };
        return new QualityObservation(
            QualityObservation.SchemaId,
            1,
            observationId,
            observedAt.ToUniversalTime(),
            taxonomy,
            null,
            new QualitySubject(unitId, EnsureHash(subjectHash), level.ToString().ToLowerInvariant(),
                QualityObservationJson.NoExtensions),
            new QualityProfile($"{level.ToString().ToLowerInvariant()}-{kind}-review", "1.0.0",
                EnsureHash(promptHash), EnsureHash(reviewInputsHash), kind, QualityObservationJson.NoExtensions),
            new QualityProducer(QualityProducerKind.Agent, Value(agent), Value(provider), Value(requestedModel),
                Value(effectiveModel), Value(thinkingLevel), Value(routePolicyVersion), runId, reviewRunId,
                QualityObservationJson.NoExtensions),
            evidenceStatus,
            evidence,
            aspects,
            assessment,
            null,
            decision,
            findings,
            null,
            extensions);
    }

    private static string MapAspect(string id, string kind)
    {
        if (id == "analyzer") return $"quality-studio:producer.{kind}-analyzer";
        return QualityLegacyMappings.MapAspect(id);
    }

    private static QualityAssessment MostSevere(QualityAssessment left, QualityAssessment right)
    {
        static int Rank(QualityAssessment value) => value switch
        {
            QualityAssessment.Fail => 5,
            QualityAssessment.Concern => 4,
            QualityAssessment.Inconclusive => 3,
            QualityAssessment.NotAssessed => 2,
            QualityAssessment.Pass => 1,
            _ => 0,
        };
        return Rank(left) >= Rank(right) ? left : right;
    }

    private static QualityGrade ParseGrade(JsonObject? grade)
    {
        var score = Math.Clamp(grade?["score"]?.GetValue<int>() ?? 0, 0, 100);
        var band = grade?["band"]?.GetValue<string>() ?? QualityReportBuilder.Grade(score);
        return new QualityGrade(score, band);
    }

    private static FindingSeverity ParseSeverity(string? value) => value switch
    {
        "critical" => FindingSeverity.Critical,
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        "low" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private static QualityEvidence LocationEvidence(string id, JsonObject location, string title)
    {
        var parsed = ParseLocation(location);
        return new QualityEvidence(id, QualityEvidenceKind.SourceCode,
            new QualityEvidenceLocator(parsed.Path, parsed.SymbolId,
                parsed.Range?.Start.Line, parsed.Range?.Start.Column,
                parsed.Range?.End.Line, parsed.Range?.End.Column),
            title, null, null, null, QualityObservationJson.NoExtensions);
    }

    private static (string? Path, string? SymbolId, FindingRange? Range) ParseLocation(JsonObject? location)
    {
        if (location is null) return (".", null, null);
        var range = location["range"]?.AsObject();
        var start = range?["start"]?.AsObject();
        var end = range?["end"]?.AsObject();
        FindingRange? parsedRange = start is null || end is null ? null : new FindingRange(
            new FindingPosition(start["line"]?.GetValue<int>() ?? 1, start["column"]?.GetValue<int>() ?? 1),
            new FindingPosition(end["line"]?.GetValue<int>() ?? 1, end["column"]?.GetValue<int>() ?? 1));
        return (location["path"]?.GetValue<string>() ?? ".", location["symbolId"]?.GetValue<string>(), parsedRange);
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    private static string EnsureHash(string value) => value.StartsWith("sha256:", StringComparison.Ordinal)
        ? value
        : "sha256:" + value;
}
