using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentOrchestrator.CodeQuality;

public sealed record ChangeReviewEvidenceDocument
{
    public const int CurrentSchemaVersion = 1;
    public const string SchemaId = "https://quality.studio/schemas/change-review-evidence.v1.schema.json";

    private const string ProviderPolicyText =
        "quality-studio-change-review-policy-v1\0grades\0findings\0staleness\0boundaries\0coverage\0churn";

    public static ChangeReviewContractReference ProviderPolicy { get; } = new(
        "quality-studio-change-review-policy",
        "1.0.0",
        Hash(ProviderPolicyText));

    [JsonPropertyName("$schema"), JsonPropertyOrder(0)]
    public string Schema { get; init; } = SchemaId;

    [JsonPropertyOrder(1)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyOrder(2)]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyOrder(3)]
    public required string Repository { get; init; }

    [JsonPropertyOrder(4)]
    public ChangeReviewContractReference Policy { get; init; } = ProviderPolicy;

    [JsonPropertyOrder(5)]
    public required IReadOnlyList<ChangeReviewEvidenceEntry> Reviews { get; init; }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record ChangeReviewContractReference(string Id, string Version, string ContentHash);

public sealed record ChangeReviewEvidenceEntry(
    QualityFindingSubject Subject,
    ChangeReviewDocument Review,
    ChangeReviewAgentEvidence AgentEvidence,
    ChangeReviewFindingChanges Findings);

[JsonConverter(typeof(JsonStringEnumConverter<ChangeReviewAgentEvidenceStatus>))]
public enum ChangeReviewAgentEvidenceStatus
{
    Complete,
    Unavailable,
}

public sealed record ChangeReviewAgentEvidence(
    ChangeReviewAgentEvidenceStatus Status,
    ChangeReviewPromptProvenance Prompt,
    IReadOnlyList<ChangeJudgementAspect> Aspects,
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RunId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TokenUsage? Usage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? UnavailableReason = null);

public sealed record ChangeReviewFindingChanges(
    IReadOnlyList<QualityFindingEnvelope> New,
    IReadOnlyList<QualityFindingEnvelope> Resolved,
    IReadOnlyList<QualityFindingEnvelope> Persisting);

public static class ChangeReviewEvidenceJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static ChangeReviewEvidenceDocument Create(
        string repository,
        string reviewPolicyHash,
        IReadOnlyList<ChangeReviewResult> results,
        DateTimeOffset? generatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(repository)) throw new ArgumentException("Repository identity is required.", nameof(repository));
        ValidateSha256(reviewPolicyHash, nameof(reviewPolicyHash));
        ArgumentNullException.ThrowIfNull(results);
        return new ChangeReviewEvidenceDocument
        {
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow,
            Repository = repository,
            Reviews = results.Select(result => CreateEntry(repository, reviewPolicyHash, result.Document)).ToArray(),
        };
    }

    public static string Serialize(ChangeReviewEvidenceDocument document)
    {
        Validate(document);
        return JsonSerializer.Serialize(document, Options) + "\n";
    }

    public static ChangeReviewEvidenceDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize<ChangeReviewEvidenceDocument>(json, Options)
            ?? throw new JsonException("Change-review evidence must be a JSON object.");
        Validate(document);
        return document;
    }

    public static async Task SaveAsync(
        string path,
        ChangeReviewEvidenceDocument document,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary, Serialize(document), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ChangeReviewEvidenceEntry CreateEntry(
        string repository,
        string reviewPolicyHash,
        ChangeReviewDocument review)
    {
        var change = review.ChangeSet;
        var subject = new TaskChangeFindingSubject(
            repository,
            change.BaseCommit,
            change.HeadCommit,
            change.MergeCommit ?? change.HeadCommit,
            reviewPolicyHash);
        var judgement = review.Judgement;
        var complete = string.Equals(judgement.Status, "complete", StringComparison.Ordinal);
        var agentEvidence = new ChangeReviewAgentEvidence(
            complete ? ChangeReviewAgentEvidenceStatus.Complete : ChangeReviewAgentEvidenceStatus.Unavailable,
            judgement.Prompt ?? AgentChangeDeltaReviewer.DefaultPromptProvenance,
            judgement.Aspects,
            judgement.Summary,
            judgement.Provider,
            complete ? judgement.Reviewer : null,
            judgement.RunId,
            judgement.Usage,
            complete ? null : judgement.UnavailableReason ?? "Agent judgement was not requested.");
        var producer = new QualityFindingProducer(
            QualityFindingProducerKind.Agent,
            "quality-studio-standing-review");
        QualityFindingEnvelope Convert(FindingDeltaItem finding) =>
            QualityFindingEnvelope.FromFindingDeltaItem(finding, subject, producer);
        var findings = new ChangeReviewFindingChanges(
            review.Delta.Findings.New.Select(Convert).ToArray(),
            review.Delta.Findings.Resolved.Select(Convert).ToArray(),
            review.Delta.Findings.Persisting.Select(Convert).ToArray());
        return new ChangeReviewEvidenceEntry(subject, review, agentEvidence, findings);
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

    private static void Validate(ChangeReviewEvidenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != ChangeReviewEvidenceDocument.CurrentSchemaVersion ||
            !string.Equals(document.Schema, ChangeReviewEvidenceDocument.SchemaId, StringComparison.Ordinal))
            throw new JsonException($"Unsupported change-review evidence schemaVersion '{document.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(document.Repository)) throw new JsonException("repository is required.");
        ValidateSha256(document.Policy.ContentHash, "policy.contentHash");
        foreach (var review in document.Reviews)
        {
            if (review.Subject is not TaskChangeFindingSubject subject)
                throw new JsonException("Portable change-review evidence requires a task-change subject.");
            if (!string.Equals(subject.ResultSha,
                    review.Review.ChangeSet.MergeCommit ?? review.Review.ChangeSet.HeadCommit,
                    StringComparison.Ordinal))
                throw new JsonException("Review subject resultSha does not match the change-review result commit.");
        }
    }

    private static void ValidateSha256(string value, string name)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            !value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException($"{name} must be a lowercase sha256 value.", name);
    }
}
