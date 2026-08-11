using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Api;

public sealed record ReviewHistoryRoute(string? Cli, string? Model, string? ThinkingLevel);

public sealed record ReviewRunOperationEvidence(
    string Path,
    string State,
    string? MetaReference,
    string? MetaHash,
    string? ReviewedHash,
    string? OperationRunId,
    ReviewHistoryRoute? Requested,
    ReviewHistoryRoute? Executed,
    IReadOnlyList<string> FindingFingerprints,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FindingFingerprintsByAspect,
    IReadOnlyDictionary<string, int> EvidenceClasses,
    IReadOnlyDictionary<string, int> ReproductionStatuses);

public sealed record ReviewHistoryPayload(
    string RunId,
    string RepositoryId,
    ReviewRunPlanNode Scope,
    string Level,
    string Kind,
    ReviewHistoryRoute RequestedRoute,
    ReviewHistoryRoute ExecutedRoute,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset FinishedAt,
    string State,
    ReviewRunEstimate? Estimate,
    long? TokenCap,
    decimal? CostCap,
    ReviewEstimateDeviation? EstimateDeviation,
    IReadOnlyList<ReviewRunPlanTarget> Targets,
    IReadOnlyList<string>? AggregateControls,
    IReadOnlyList<ScopeExclusion>? AggregateExclusions,
    bool Force,
    IReadOnlyList<ReviewRunFileTransition> Outcomes,
    string? AggregateState,
    int UsageOperations,
    TokenUsage Usage,
    decimal? CostSpent,
    string? Currency,
    string PriceStatus,
    IReadOnlyList<ReviewRunOperationEvidence> Evidence,
    IReadOnlyList<string> Errors,
    string? StopReason);

public sealed record ReviewHistoryEnvelope(int SchemaVersion, string ContentHash, ReviewHistoryPayload Run);

public sealed class ReviewHistoryConflictException(string runId)
    : Exception($"Committed review history for '{runId}' differs from the terminal run payload.");

/// <summary>Repository-owned, create-only terminal review history suitable for source control.</summary>
public sealed class ReviewHistoryStore
{
    public const string RelativeRunsPath = ".quality/review-history/runs";
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PrettyJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string repositoryRoot;
    private readonly string historyPath;

    public ReviewHistoryStore(string repositoryRoot)
    {
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        historyPath = Path.Combine(this.repositoryRoot, RelativeRunsPath.Replace('/', Path.DirectorySeparatorChar));
    }

    public string HistoryPath => historyPath;

    public static bool IsCommittable(string state) => state is "done" or "failed" or "cancelled";

    public ReviewHistoryEnvelope Commit(ReviewRunManifest manifest, ReviewRunStatus status,
        IReadOnlyList<ReviewRunFileTransition> progress, IReadOnlyList<ReviewRunOperationEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(status);
        if (!IsCommittable(status.State) || status.FinishedAt is null)
            throw new ArgumentException("Only a finished terminal review run can be committed to history.");
        if (!string.Equals(manifest.RunId, status.RunId, StringComparison.Ordinal))
            throw new ArgumentException("History manifest and status run ids differ.");
        ValidateIdentifier(manifest.RunId);
        var payload = new ReviewHistoryPayload(
            manifest.RunId,
            manifest.RepositoryId,
            manifest.Node with { Path = NormalizeRelativeOrRoot(manifest.Node.Path) },
            manifest.Level,
            manifest.Kind,
            new(manifest.RequestedCliType, manifest.RequestedModel, manifest.RequestedThinkingLevel),
            new(manifest.CliType, manifest.Model ?? "runner-default", manifest.ThinkingLevel ?? "model-default"),
            status.CreatedAt,
            status.StartedAt,
            status.FinishedAt.Value,
            status.State,
            manifest.Estimate,
            status.TokenCap,
            status.CostCap,
            Deviation(manifest.Estimate, status),
            manifest.Targets.Select(target => target with { Path = NormalizeRelative(target.Path) }).ToArray(),
            manifest.AggregateControls?.Select(NormalizeRelativeOrRoot).ToArray(),
            manifest.AggregateExclusions?.Select(exclusion => exclusion with { Path = NormalizeRelative(exclusion.Path) }).ToArray(),
            manifest.Force,
            progress.Select(item => item with { Path = NormalizeRelative(item.Path), Error = Sanitize(item.Error) }).ToArray(),
            status.AggregateState,
            status.UsageOperations,
            status.Usage,
            status.CostSpent,
            status.Currency,
            status.PriceStatus,
            evidence.Select(ValidateEvidence).ToArray(),
            status.Errors.Select(error => Sanitize(error)!).ToArray(),
            Sanitize(status.StopReason));
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, CompactJson);
        var envelope = new ReviewHistoryEnvelope(1,
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(payloadBytes)), payload);
        Directory.CreateDirectory(historyPath);
        PathConfinement.RejectReparseTraversal(repositoryRoot, historyPath);
        var destination = Path.Combine(historyPath, manifest.RunId + ".json");
        if (File.Exists(destination)) return VerifyExisting(destination, envelope);
        var temporary = Path.Combine(historyPath, $".{manifest.RunId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = JsonSerializer.Serialize(envelope, PrettyJson) + Environment.NewLine;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = Utf8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { return VerifyExisting(destination, envelope); }
            return envelope;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<ReviewHistoryEnvelope> LoadAll(Action<string, Exception>? loadFailed = null)
    {
        if (!Directory.Exists(historyPath)) return [];
        PathConfinement.RejectReparseTraversal(repositoryRoot, historyPath);
        var result = new List<ReviewHistoryEnvelope>();
        foreach (var path in Directory.EnumerateFiles(historyPath, "review-*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<ReviewHistoryEnvelope>(File.ReadAllText(path), PrettyJson)
                    ?? throw new InvalidDataException("Review history document is empty.");
                ValidateIdentifier(envelope.Run.RunId);
                if (envelope.SchemaVersion != 1 || Path.GetFileNameWithoutExtension(path) != envelope.Run.RunId ||
                    !IsCommittable(envelope.Run.State))
                    throw new InvalidDataException("Review history envelope is invalid.");
                var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(envelope.Run, CompactJson)));
                if (!string.Equals(actualHash, envelope.ContentHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Review history content hash does not match its payload.");
                result.Add(envelope);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                loadFailed?.Invoke(path, exception);
            }
        }
        return result.OrderByDescending(item => item.Run.FinishedAt).ToArray();
    }

    private ReviewHistoryEnvelope VerifyExisting(string path, ReviewHistoryEnvelope expected)
    {
        var existing = JsonSerializer.Deserialize<ReviewHistoryEnvelope>(File.ReadAllText(path), PrettyJson)
            ?? throw new ReviewHistoryConflictException(expected.Run.RunId);
        var actualHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(existing.Run, CompactJson)));
        if (existing.SchemaVersion != 1 || existing.ContentHash != actualHash ||
            existing.ContentHash != expected.ContentHash)
            throw new ReviewHistoryConflictException(expected.Run.RunId);
        return existing;
    }

    private ReviewRunOperationEvidence ValidateEvidence(ReviewRunOperationEvidence evidence) => evidence with
    {
        Path = NormalizeRelativeOrRoot(evidence.Path),
        MetaReference = evidence.MetaReference is null ? null : NormalizeRelative(evidence.MetaReference),
    };

    private static ReviewEstimateDeviation? Deviation(ReviewRunEstimate? estimate, ReviewRunStatus status)
    {
        if (status.State != "done" || estimate is null || status.Usage.InputTokens is null || status.Usage.OutputTokens is null)
            return null;
        return new ReviewEstimateDeviation(
            Percent(status.Usage.InputTokens.Value, estimate.InputTokens),
            Percent(status.Usage.OutputTokens.Value, estimate.OutputTokens),
            status.CostSpent.HasValue && estimate.Cost.HasValue
                ? Percent(status.CostSpent.Value, estimate.Cost.Value)
                : null,
            "Positive means actual was above preflight; prompt tokenizer, CLI context, caching, and response length cause deviation.");
    }

    private static decimal Percent(decimal actual, decimal estimate) =>
        estimate == 0 ? 0 : Math.Round((actual - estimate) / estimate * 100m, 2);

    private string? Sanitize(string? value)
    {
        if (value is null) return null;
        var sanitized = value.Replace(repositoryRoot, "<repository>", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    private static string NormalizeRelativeOrRoot(string value) => value == "." ? value : NormalizeRelative(value);

    private static string NormalizeRelative(string value)
    {
        if (Path.IsPathRooted(value) || value.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Review history paths must be normalized and repository-relative.");
        return value.Replace('\\', '/');
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException("Review history run id is invalid.");
    }
}
