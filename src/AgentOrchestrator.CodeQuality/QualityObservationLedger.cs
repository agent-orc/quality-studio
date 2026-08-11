using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentOrchestrator.CodeQuality;

/// <summary>Append-only, repository-local immutable quality observations.</summary>
public static class QualityObservationLedger
{
    public const string RelativeDirectory = ".quality/observations";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions LineOptions = new(QualityObservationJson.Options)
    {
        WriteIndented = false,
    };

    public static string GetLedgerPath(string repositoryRoot, DateTimeOffset observedAt) =>
        Path.Combine(
            Path.GetFullPath(repositoryRoot),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar),
            observedAt.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    public static string CreateObservationId(
        string runId,
        string unitId,
        string kind,
        string subjectHash,
        string reviewInputsHash,
        string taxonomyDigest)
    {
        var canonical = string.Join('\0',
            "quality-studio-observation-id-v1",
            runId,
            unitId,
            kind,
            subjectHash,
            reviewInputsHash,
            taxonomyDigest);
        return "observation-sha256:" +
               Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static async Task<bool> AppendAsync(
        string repositoryRoot,
        QualityObservationDocument observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(observation);
        _ = QualityObservationJson.Serialize(observation);

        var directory = Path.Combine(
            Path.GetFullPath(repositoryRoot),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ContainsAsync(directory, observation.ObservationId, cancellationToken).ConfigureAwait(false))
                return false;

            Directory.CreateDirectory(directory);
            var path = GetLedgerPath(repositoryRoot, observation.ObservedAt);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(observation, LineOptions) + "\n");
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<IReadOnlyList<QualityObservationDocument>> ReadAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            Path.GetFullPath(repositoryRoot),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return [];

        var observations = new List<QualityObservationDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var result = QualityObservationJson.ReadPreservingUnsupported(line);
                    if (result.Observation is not null) observations.Add(result.Observation);
                }
                catch (JsonException)
                {
                    // A partial or malformed historical line must not hide later immutable observations.
                }
            }
        }
        return observations;
    }

    public static IReadOnlyList<QualityObservationDocument> Read(string repositoryRoot)
    {
        var directory = Path.Combine(
            Path.GetFullPath(repositoryRoot),
            RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return [];

        var observations = new List<QualityObservationDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "????-??.jsonl", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var result = QualityObservationJson.ReadPreservingUnsupported(line);
                    if (result.Observation is not null) observations.Add(result.Observation);
                }
                catch (JsonException)
                {
                    // A partial or malformed historical line must not hide later immutable observations.
                }
            }
        }
        return observations;
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
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("observationId", out var id) &&
                        string.Equals(id.GetString(), observationId, StringComparison.Ordinal))
                        return true;
                }
                catch (JsonException)
                {
                    // Ignore malformed historical lines while looking for a durable identity.
                }
            }
        }
        return false;
    }
}

public sealed record GeneralReviewObservationContext(
    string UnitId,
    string RelativePath,
    string Kind,
    ReviewLevel Level,
    string SubjectHash,
    string ReviewInputsHash,
    string RunId,
    string ReviewRunId,
    string Agent,
    string Provider,
    string RequestedModel,
    string EffectiveModel,
    string ThinkingLevel,
    string RoutePolicyVersion,
    DateTimeOffset ObservedAt,
    IReadOnlyList<SubjectInputHash> SubjectInputs,
    SecurityEvidenceBundle SecurityEvidence,
    JsonObject? CurrentProjection = null);

public static class GeneralReviewObservationAdapter
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoExtensions =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public static QualityObservationDocument Create(JsonObject response, GeneralReviewObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(context);
        var promptHash = ReviewPromptBuilder.TemplateHash(context.Kind);
        var observationId = QualityObservationLedger.CreateObservationId(
            context.RunId,
            context.UnitId,
            context.Kind,
            context.SubjectHash,
            context.ReviewInputsHash,
            QualityTaxonomyCatalogue.CoreDigest);
        var evidence = new List<QualityEvidence>();
        var findings = CreateFindings(response, context, evidence);
        var aspects = response["aspects"]!.AsArray().OfType<JsonObject>()
            .Where(aspect => !string.Equals(aspect["id"]?.GetValue<string>(), "sensor-availability", StringComparison.Ordinal))
            .Select(aspect => CreateAspect(context.Kind, aspect))
            .ToArray();
        var grade = response["grade"]!.AsObject();
        var assessment = Assessment(grade);
        var evidenceStatus = "available";
        QualityObservationDecision? decision = null;
        if (string.Equals(context.Kind, "security", StringComparison.Ordinal) &&
            context.SecurityEvidence.Sensors.Count > 0)
        {
            var mapping = QualityLegacyMapper.Map(
                LegacyQualityVocabulary.SecurityVerdict,
                SecurityEvidenceBundle.VerdictName(context.SecurityEvidence.Verdict));
            assessment = mapping.Assessment!;
            evidenceStatus = mapping.EvidenceStatus!;
            if (mapping.Decision is not null)
                decision = new QualityObservationDecision(mapping.Decision, mapping.PolicyRef!);
        }

        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["quality-studio:review-summary"] = JsonSerializer.SerializeToElement(
                response["summary"]!.GetValue<string>()),
            ["quality-studio:review-kind"] = JsonSerializer.SerializeToElement(context.Kind),
            ["quality-studio:review-level"] = JsonSerializer.SerializeToElement(
                context.Level.ToString().ToLowerInvariant()),
        };
        if (context.CurrentProjection is not null)
            extensions[QualityObservationReducer.ProjectionExtension] =
                JsonSerializer.SerializeToElement(context.CurrentProjection);
        return new QualityObservationDocument
        {
            ObservationId = observationId,
            ObservedAt = context.ObservedAt,
            Taxonomy = QualityTaxonomyCatalogue.CoreReference,
            Subject = new QualityObservationSubject(
                context.UnitId,
                PrefixHash(context.SubjectHash),
                "unit",
                NoExtensions),
            Profile = new QualityObservationProfile(
                $"file-{context.Kind}-review",
                "1.0.0",
                promptHash,
                PrefixHash(context.ReviewInputsHash),
                NoExtensions),
            Producer = new QualityObservationProducer(
                "agent",
                ValueOrUnknown(context.Agent),
                ValueOrUnknown(context.Provider),
                ValueOrUnknown(context.RequestedModel),
                ValueOrUnknown(context.EffectiveModel),
                ValueOrUnknown(context.ThinkingLevel),
                ValueOrUnknown(context.RoutePolicyVersion),
                context.RunId,
                context.ReviewRunId,
                NoExtensions),
            EvidenceStatus = evidenceStatus,
            Evidence = evidence,
            Aspects = aspects,
            Assessment = assessment,
            Decision = decision,
            Findings = findings,
            Extensions = extensions,
        };
    }

    private static IReadOnlyList<QualityObservationFinding> CreateFindings(
        JsonObject response,
        GeneralReviewObservationContext context,
        List<QualityEvidence> evidence)
    {
        var contentHashes = context.SubjectInputs
            .Where(input => input.Selector == "file")
            .ToDictionary(input => input.Path, input => input.ContentHash, StringComparer.Ordinal);
        var result = new List<QualityObservationFinding>();
        foreach (var finding in response["findings"]!.AsArray().OfType<JsonObject>())
        {
            var findingId = finding["id"]!.GetValue<string>();
            var evidenceRefs = new List<string>();
            var locations = finding["locations"]!.AsArray().OfType<JsonObject>().ToArray();
            for (var index = 0; index < locations.Length; index++)
            {
                var location = locations[index];
                var path = location["path"]!.GetValue<string>();
                var range = location["range"] as JsonObject;
                var start = range?["start"] as JsonObject;
                var evidenceId = $"{findingId}-location-{index + 1}";
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    "source-code",
                    new QualityEvidenceLocator(
                        Path: path,
                        SymbolId: location["symbolId"]?.GetValue<string>(),
                        Line: start?["line"]?.GetValue<int>(),
                        Column: start?["column"]?.GetValue<int>()),
                    finding["description"]!.GetValue<string>(),
                    contentHashes.GetValueOrDefault(path),
                    Extensions: NoExtensions));
                evidenceRefs.Add(evidenceId);
            }
            if (finding["evidence"] is JsonValue evidenceValue &&
                evidenceValue.TryGetValue<string>(out var legacyEvidence) &&
                !string.IsNullOrWhiteSpace(legacyEvidence))
            {
                var evidenceId = $"{findingId}-legacy-evidence";
                evidence.Add(QualityLegacyMapper.MapEvidence(
                    evidenceId,
                    legacyEvidence,
                    new QualityEvidenceLocator(Path: locations.FirstOrDefault()?["path"]?.GetValue<string>() ?? context.RelativePath)));
                evidenceRefs.Add(evidenceId);
            }
            if (evidenceRefs.Count == 0)
            {
                var evidenceId = $"{findingId}-subject";
                evidence.Add(new QualityEvidence(
                    evidenceId,
                    "source-code",
                    new QualityEvidenceLocator(Path: context.RelativePath),
                    finding["description"]!.GetValue<string>(),
                    contentHashes.GetValueOrDefault(context.RelativePath),
                    Extensions: NoExtensions));
                evidenceRefs.Add(evidenceId);
            }

            var fingerprint = finding["fingerprint"]!.GetValue<string>();
            var occurrenceFingerprint = FindingIdentity.OccurrenceFingerprint(fingerprint);
            var source = finding["source"]?["kind"]?.GetValue<string>() == "deterministic"
                ? "deterministic-sensor"
                : "agent";
            var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["quality-studio:title"] = JsonSerializer.SerializeToElement(finding["title"]!.GetValue<string>()),
                ["quality-studio:description"] = JsonSerializer.SerializeToElement(finding["description"]!.GetValue<string>()),
                ["quality-studio:recommendation"] = JsonSerializer.SerializeToElement(finding["recommendation"]!.GetValue<string>()),
                ["quality-studio:locations"] = ToElement(finding["locations"]!),
            };
            result.Add(new QualityObservationFinding(
                findingId,
                FindingIdentity.IssueId(occurrenceFingerprint),
                occurrenceFingerprint,
                FindingIdentity.OccurrenceCanonicalization,
                finding["ruleId"]!.GetValue<string>(),
                CanonicalAspect(context.Kind, finding["aspect"]!.GetValue<string>()),
                finding["severity"]!.GetValue<string>(),
                evidenceRefs,
                new QualityFindingSource(source, "self", NoExtensions),
                FingerprintAliases: [fingerprint],
                Extensions: extensions));
        }
        return result;
    }

    private static QualityObservationAspect CreateAspect(string kind, JsonObject aspect)
    {
        var grade = aspect["grade"]!.AsObject();
        return new QualityObservationAspect(
            CanonicalAspect(kind, aspect["id"]!.GetValue<string>()),
            Assessment(grade),
            grade["rationale"]!.GetValue<string>(),
            Grade: new QualityObservationGrade(
                grade["score"]!.GetValue<int>(),
                grade["band"]!.GetValue<string>()),
            Extensions: NoExtensions);
    }

    public static string CanonicalAspect(string kind, string legacyAspect) => (kind, legacyAspect) switch
    {
        (_, var value) when value.Contains('.', StringComparison.Ordinal) || value.Contains(':', StringComparison.Ordinal) => value,
        ("code", "correctness") => "code.correctness",
        ("code", "architecture") => "code.architecture",
        ("security", "security") => "security.general",
        ("security", "secrets") => "security.secrets",
        ("security", "dependencies") => "security.dependencies",
        ("security", "authentication-authorization") => "security.authentication-authorization",
        ("security", "input-validation") => "security.input-validation",
        ("security", "configuration-iac") => "security.configuration-iac",
        ("security", "boundaries") => "security.boundary-exposure",
        ("performance", "performance") => "performance.general",
        (_, "analyzer") => "quality-studio:producer.analyzer",
        _ => $"quality-studio:{kind}.{legacyAspect}",
    };

    private static string Assessment(JsonObject grade) => grade["band"]!.GetValue<string>() switch
    {
        "A" or "B" => "pass",
        "C" => "concern",
        "D" or "F" => "fail",
        _ => "inconclusive",
    };

    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string PrefixHash(string value) =>
        value.StartsWith("sha256:", StringComparison.Ordinal) ? value : "sha256:" + value;

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;
}
