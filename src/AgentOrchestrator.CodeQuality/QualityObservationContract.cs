using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// One producer's immutable result for one exact subject and input set (quality-observation.v1,
/// dossier docs/operations/data-model-taxonomy T1). Observations are append-only: a rerun creates
/// another observation, it never overwrites or deletes an earlier one. This type only models the
/// contract; writing observations to <c>.quality/observations/</c> is T2's scope.
/// </summary>
public sealed record QualityObservationDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-observation.v1.schema.json";

    public string Schema { get; init; } = SchemaId;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ObservationId { get; init; }
    public required QualityObservationTaxonomyReference Taxonomy { get; init; }
    public required QualityObservationSubject Subject { get; init; }
    public required QualityObservationProfile Profile { get; init; }
    public required QualityObservationProducer Producer { get; init; }
    public required string EvidenceStatus { get; init; }
    public required IReadOnlyList<QualityObservationEvidence> Evidence { get; init; }
    public required IReadOnlyList<QualityObservationAspectResult> Aspects { get; init; }
    public required string Assessment { get; init; }
    public required IReadOnlyList<QualityObservationFinding> Findings { get; init; }

    /// <summary>Namespaced extension terms preserved as raw JSON, keyed by their declared id.</summary>
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>Legacy root <c>x-*</c> keys, preserved as raw JSON for read compatibility only.</summary>
    public IReadOnlyDictionary<string, JsonElement> LegacyExtensions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record QualityObservationTaxonomyReference(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string Digest);

public sealed record QualityObservationSubject(
    [property: JsonPropertyOrder(0)] string UnitId,
    [property: JsonPropertyOrder(1)] string ManifestHash);

public sealed record QualityObservationProfile(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Version,
    [property: JsonPropertyOrder(2)] string PromptHash,
    [property: JsonPropertyOrder(3)] string ReviewInputsHash);

public sealed record QualityObservationProducer(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1)] string RequestedModel,
    [property: JsonPropertyOrder(2)] string EffectiveModel,
    [property: JsonPropertyOrder(3)] string ThinkingLevel,
    [property: JsonPropertyOrder(4)] string RoutePolicyVersion,
    [property: JsonPropertyOrder(5)] string RunId,
    [property: JsonPropertyOrder(6), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Agent = null,
    [property: JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReviewRunId = null);

public sealed record QualityObservationEvidence(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Kind,
    [property: JsonPropertyOrder(2)] IReadOnlyDictionary<string, JsonElement> Locator,
    [property: JsonPropertyOrder(3)] string Summary,
    [property: JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentHash = null);

public sealed record QualityObservationAspectResult(
    [property: JsonPropertyOrder(0)] string AspectId,
    [property: JsonPropertyOrder(1)] string Assessment,
    [property: JsonPropertyOrder(2)] string Rationale,
    [property: JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] QualityObservationGrade? Grade = null);

public sealed record QualityObservationGrade(
    [property: JsonPropertyOrder(0)] int Score,
    [property: JsonPropertyOrder(1)] GradeBand Band);

public sealed record QualityObservationFindingSource(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1)] string ProducerRef);

public sealed record QualityObservationFinding(
    [property: JsonPropertyOrder(0)] string ObservationFindingId,
    [property: JsonPropertyOrder(1)] string IssueId,
    [property: JsonPropertyOrder(2)] string OccurrenceFingerprint,
    [property: JsonPropertyOrder(3)] string FingerprintAlgorithm,
    [property: JsonPropertyOrder(4)] string AspectId,
    [property: JsonPropertyOrder(5)] FindingSeverity Severity,
    [property: JsonPropertyOrder(6)] IReadOnlyList<string> EvidenceRefs,
    [property: JsonPropertyOrder(7)] QualityObservationFindingSource Source,
    [property: JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RuleRef = null);

/// <summary>
/// The result of parsing an observation document. An unsupported schema major or taxonomy major is
/// quarantined as structured-but-unsupported: <see cref="RawDocument"/> stays inspectable and no
/// document, verdict, or grade is inferred from it (dossier acceptance invariant, section 12).
/// </summary>
public sealed class QualityObservationParseResult : IDisposable
{
    public required bool IsSupported { get; init; }
    public string? UnsupportedReason { get; init; }
    public QualityObservationDocument? Document { get; init; }
    public required JsonDocument RawDocument { get; init; }

    public void Dispose() => RawDocument.Dispose();
}

public static class QualityObservationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(QualityObservationDocument document)
    {
        Validate(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", document.Schema);
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteString("observationId", document.ObservationId);
            writer.WritePropertyName("taxonomy");
            JsonSerializer.Serialize(writer, document.Taxonomy, Options);
            writer.WritePropertyName("subject");
            JsonSerializer.Serialize(writer, document.Subject, Options);
            writer.WritePropertyName("profile");
            JsonSerializer.Serialize(writer, document.Profile, Options);
            writer.WritePropertyName("producer");
            JsonSerializer.Serialize(writer, document.Producer, Options);
            writer.WriteString("evidenceStatus", document.EvidenceStatus);
            writer.WritePropertyName("evidence");
            JsonSerializer.Serialize(writer, document.Evidence, Options);
            writer.WritePropertyName("aspects");
            JsonSerializer.Serialize(writer, document.Aspects, Options);
            writer.WriteString("assessment", document.Assessment);
            writer.WritePropertyName("findings");
            JsonSerializer.Serialize(writer, document.Findings, Options);
            if (document.Extensions.Count > 0)
            {
                writer.WritePropertyName("extensions");
                writer.WriteStartObject();
                foreach (var pair in document.Extensions.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    pair.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            foreach (var pair in document.LegacyExtensions.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses an observation document, quarantining unsupported schema or taxonomy majors instead of
    /// throwing, so raw JSON is never discarded (dossier acceptance invariant, section 12).
    /// </summary>
    public static QualityObservationParseResult Parse(string json)
    {
        var rawDocument = JsonDocument.Parse(json);
        try
        {
            var root = rawDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unsupported(rawDocument, "Quality observation must be a JSON object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number)
            {
                return Unsupported(rawDocument, "Quality observation is missing an integer 'schemaVersion'.");
            }

            var schemaVersion = schemaVersionElement.GetInt32();
            if (schemaVersion != QualityObservationDocument.CurrentSchemaVersion)
            {
                return Unsupported(rawDocument, $"Unsupported quality-observation schemaVersion '{schemaVersion}'.");
            }

            if (!root.TryGetProperty("taxonomy", out var taxonomyElement))
            {
                return Unsupported(rawDocument, "Quality observation is missing 'taxonomy'.");
            }

            var taxonomy = JsonSerializer.Deserialize<QualityObservationTaxonomyReference>(taxonomyElement, Options)
                ?? throw new JsonException("Quality observation 'taxonomy' must be an object.");
            var installedMajor = MajorVersion(QualityTaxonomyCatalogue.Current.Value.Version);
            var observedMajor = MajorVersion(taxonomy.Version);
            if (!string.Equals(taxonomy.Id, QualityTaxonomyCatalogue.TaxonomyId, StringComparison.Ordinal) ||
                observedMajor != installedMajor)
            {
                return Unsupported(rawDocument,
                    $"Unsupported taxonomy major '{taxonomy.Id}@{taxonomy.Version}'; installed major is '{installedMajor}'.");
            }

            var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var legacyExtensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("extensions"))
                {
                    foreach (var item in property.Value.EnumerateObject())
                    {
                        extensions[item.Name] = item.Value.Clone();
                    }
                }
                else if (property.Name.StartsWith("x-", StringComparison.Ordinal))
                {
                    legacyExtensions[property.Name] = property.Value.Clone();
                }
            }

            var document = new QualityObservationDocument
            {
                Schema = root.GetProperty("$schema").GetString()
                    ?? throw new JsonException("Quality observation '$schema' must be a string."),
                SchemaVersion = schemaVersion,
                ObservationId = root.GetProperty("observationId").GetString()
                    ?? throw new JsonException("Quality observation 'observationId' must be a string."),
                Taxonomy = taxonomy,
                Subject = Require<QualityObservationSubject>(root, "subject")
                    ?? throw new JsonException("Quality observation 'subject' must be an object."),
                Profile = Require<QualityObservationProfile>(root, "profile")
                    ?? throw new JsonException("Quality observation 'profile' must be an object."),
                Producer = Require<QualityObservationProducer>(root, "producer")
                    ?? throw new JsonException("Quality observation 'producer' must be an object."),
                EvidenceStatus = root.GetProperty("evidenceStatus").GetString()
                    ?? throw new JsonException("Quality observation 'evidenceStatus' must be a string."),
                Evidence = Require<IReadOnlyList<QualityObservationEvidence>>(root, "evidence") ?? [],
                Aspects = Require<IReadOnlyList<QualityObservationAspectResult>>(root, "aspects")
                    ?? throw new JsonException("Quality observation 'aspects' must have at least one entry."),
                Assessment = root.GetProperty("assessment").GetString()
                    ?? throw new JsonException("Quality observation 'assessment' must be a string."),
                Findings = Require<IReadOnlyList<QualityObservationFinding>>(root, "findings") ?? [],
                Extensions = extensions,
                LegacyExtensions = legacyExtensions,
            };
            ValidateTerms(document);

            var supported = new QualityObservationParseResult
            {
                IsSupported = true,
                Document = document,
                RawDocument = rawDocument,
            };
            return supported;
        }
        catch
        {
            rawDocument.Dispose();
            throw;
        }
    }

    private static QualityObservationParseResult Unsupported(JsonDocument rawDocument, string reason) =>
        new() { IsSupported = false, UnsupportedReason = reason, RawDocument = rawDocument };

    private static T? Require<T>(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
            ? JsonSerializer.Deserialize<T>(element, Options)
            : default;

    private static int MajorVersion(string semanticVersion) =>
        int.Parse(semanticVersion[..semanticVersion.IndexOf('.', StringComparison.Ordinal)],
            System.Globalization.CultureInfo.InvariantCulture);

    private static void Validate(QualityObservationDocument document)
    {
        if (document.SchemaVersion != QualityObservationDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, QualityObservationDocument.SchemaId, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported quality-observation schemaVersion '{document.SchemaVersion}'.");
        }

        ValidateTerms(document);
    }

    private static void ValidateTerms(QualityObservationDocument document)
    {
        var catalogue = QualityTaxonomyCatalogue.Current.Value;
        RequireTerm(catalogue.Axes.EvidenceStatus, document.EvidenceStatus, "evidenceStatus");
        RequireTerm(catalogue.Axes.Assessment, document.Assessment, "assessment");
        RequireTerm(catalogue.Axes.ProducerKind, document.Producer.Kind, "producer.kind");

        foreach (var aspect in document.Aspects)
        {
            RequireTerm(catalogue.Axes.Assessment, aspect.Assessment, $"aspects[{aspect.AspectId}].assessment");
        }

        foreach (var finding in document.Findings)
        {
            RequireTerm(catalogue.Axes.ProducerKind, finding.Source.Kind, $"findings[{finding.ObservationFindingId}].source.kind");
        }
    }

    private static void RequireTerm(IReadOnlyList<QualityTaxonomyTerm> axis, string value, string fieldPath)
    {
        if (!axis.Any(term => string.Equals(term.Id, value, StringComparison.Ordinal)))
        {
            throw new JsonException($"'{value}' is not a known term for '{fieldPath}' in taxonomy quality-studio/core.");
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new ObservationGradeBandConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class ObservationGradeBandConverter : JsonConverter<GradeBand>
    {
        public override GradeBand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Enum.TryParse<GradeBand>(reader.GetString(), false, out var band)
                ? band
                : throw new JsonException("grade.band must be A, B, C, D, or F.");

        public override void Write(Utf8JsonWriter writer, GradeBand value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Excludes extension aspects from core aggregation unless a matching catalogue is installed
/// (dossier "Extension contract", section 6). T1 ships the exclusion rule only; T4 wires it into
/// report projections.
/// </summary>
public static class QualityObservationAggregation
{
    public static IReadOnlyList<QualityObservationAspectResult> CoreAspectResults(QualityObservationDocument document)
    {
        var catalogue = QualityTaxonomyCatalogue.Current.Value;
        return document.Aspects
            .Where(aspect => QualityTaxonomyCatalogue.IsKnownAspect(catalogue, aspect.AspectId))
            .ToArray();
    }

    public static IReadOnlyList<QualityObservationAspectResult> UnrecognizedAspectResults(QualityObservationDocument document)
    {
        var catalogue = QualityTaxonomyCatalogue.Current.Value;
        return document.Aspects
            .Where(aspect => !QualityTaxonomyCatalogue.IsKnownAspect(catalogue, aspect.AspectId))
            .ToArray();
    }
}
