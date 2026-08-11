using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentOrchestrator.CodeQuality;

public sealed record QualityFindingEnvelope
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/quality-finding.v1.schema.json";
    public const string TaskTextFingerprintCanonicalization = "quality-studio-task-finding-text-v1";

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required QualityFindingSubject Subject { get; init; }

    [JsonPropertyOrder(3)]
    public required string Id { get; init; }

    [JsonPropertyOrder(4)]
    public required string Aspect { get; init; }

    [JsonPropertyOrder(5)]
    public required FindingSeverity Severity { get; init; }

    [JsonPropertyOrder(6)]
    public required string Title { get; init; }

    [JsonPropertyOrder(7)]
    public required string Description { get; init; }

    [JsonPropertyOrder(8)]
    public required string Recommendation { get; init; }

    [JsonPropertyOrder(9)]
    public required IReadOnlyList<FindingLocation> Locations { get; init; }

    [JsonPropertyOrder(10)]
    public required string Fingerprint { get; init; }

    [JsonPropertyOrder(11)]
    public required string FingerprintCanonicalization { get; init; }

    [JsonPropertyOrder(12)]
    public required string RuleId { get; init; }

    [JsonPropertyOrder(13)]
    public required QualityFindingProducer Producer { get; init; }

    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Evidence { get; init; }

    public static QualityFindingEnvelope FromReviewFinding(
        ReviewFinding finding,
        QualityFindingSubject subject,
        QualityFindingProducer producer,
        string fingerprintCanonicalization = FindingIdentity.Canonicalization)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(producer);
        return new QualityFindingEnvelope
        {
            Subject = subject,
            Id = finding.Id,
            Aspect = finding.Aspect,
            Severity = finding.Severity,
            Title = finding.Title,
            Description = finding.Description,
            Recommendation = finding.Recommendation,
            Locations = finding.Locations,
            Fingerprint = finding.Fingerprint,
            FingerprintCanonicalization = fingerprintCanonicalization,
            RuleId = finding.RuleId,
            Producer = producer,
            Evidence = finding.Evidence,
        };
    }

    public static QualityFindingEnvelope FromFindingDeltaItem(
        FindingDeltaItem finding,
        TaskChangeFindingSubject subject,
        QualityFindingProducer? producer = null,
        string fingerprintCanonicalization = FindingIdentity.Canonicalization)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(subject);
        var severity = Enum.TryParse<FindingSeverity>(finding.Severity, true, out var parsedSeverity)
            ? parsedSeverity
            : FindingSeverity.Info;
        var fingerprint = IsSha256(finding.Identity)
            ? finding.Identity
            : ComputeTaskTextFingerprint(finding.RuleId, finding.Title,
                finding.Description ?? $"{finding.Kind} finding observed for '{finding.UnitId}'.",
                finding.Recommendation ?? "Review the referenced Quality Studio finding and address it if it still applies.");
        var canonicalization = IsSha256(finding.Identity)
            ? fingerprintCanonicalization
            : TaskTextFingerprintCanonicalization;

        return new QualityFindingEnvelope
        {
            Subject = subject,
            Id = NormalizeId(finding.Identity),
            Aspect = string.IsNullOrWhiteSpace(finding.Aspect) ? finding.Kind : finding.Aspect,
            Severity = severity,
            Title = finding.Title,
            Description = finding.Description ?? $"{finding.Kind} finding observed for '{finding.UnitId}'.",
            Recommendation = finding.Recommendation ??
                             "Review the referenced Quality Studio finding and address it if it still applies.",
            Locations = finding.Locations is { Count: > 0 }
                ? finding.Locations
                : [new FindingLocation(finding.UnitPath)],
            Fingerprint = fingerprint,
            FingerprintCanonicalization = canonicalization,
            RuleId = finding.RuleId,
            Producer = producer ?? new QualityFindingProducer(
                QualityFindingProducerKind.Agent, "quality-studio-standing-review"),
            Evidence = finding.Evidence,
        };
    }

    public static string ComputeTaskTextFingerprint(
        string ruleId,
        string title,
        string description,
        string recommendation)
    {
        var canonical = string.Join('\0',
            TaskTextFingerprintCanonicalization,
            NormalizeText(ruleId),
            NormalizeText(title),
            NormalizeText(description),
            NormalizeText(recommendation));
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeText(string value) =>
        Regex.Replace(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(), @"\s+", " ");

    private static bool IsSha256(string value) =>
        value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string NormalizeId(string value)
    {
        if (value.StartsWith("sha256:", StringComparison.Ordinal)) return "finding-" + value[7..];
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9._-]+", "-").Trim('-');
        if (normalized.Length >= 3 && normalized[0] is >= 'a' and <= 'z') return normalized[..Math.Min(128, normalized.Length)];
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return "finding-" + hash;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StandingUnitFindingSubject), "standing-unit")]
[JsonDerivedType(typeof(TaskChangeFindingSubject), "task-change")]
public abstract record QualityFindingSubject;

public sealed record StandingUnitFindingSubject(
    string Repository,
    string UnitId,
    string Path,
    ReviewKind Kind,
    ManifestHash ReviewedHash) : QualityFindingSubject;

public sealed record TaskChangeFindingSubject(
    string Repository,
    string BaseSha,
    string HeadSha,
    string ResultSha,
    string ReviewPolicyHash) : QualityFindingSubject;

[JsonConverter(typeof(JsonStringEnumConverter<QualityFindingProducerKind>))]
public enum QualityFindingProducerKind
{
    Agent,
    Deterministic,
    Imported,
}

public sealed record QualityFindingProducer(
    QualityFindingProducerKind Kind,
    string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null);

public static class QualityFindingJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(QualityFindingEnvelope finding)
    {
        Validate(finding);
        return JsonSerializer.Serialize(finding, Options);
    }

    public static QualityFindingEnvelope Deserialize(string json)
    {
        var finding = JsonSerializer.Deserialize<QualityFindingEnvelope>(json, Options)
            ?? throw new JsonException("Quality finding must be a JSON object.");
        Validate(finding);
        return finding;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private static void Validate(QualityFindingEnvelope finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (finding.SchemaVersion != QualityFindingEnvelope.CurrentSchemaVersion ||
            !string.Equals(finding.Schema, QualityFindingEnvelope.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported quality finding schemaVersion '{finding.SchemaVersion}'.");
        if (finding.Locations is null)
            throw new JsonException("Quality finding locations must be present, including when empty.");
        if (string.IsNullOrWhiteSpace(finding.FingerprintCanonicalization))
            throw new JsonException("Quality finding fingerprintCanonicalization is required.");
    }
}
