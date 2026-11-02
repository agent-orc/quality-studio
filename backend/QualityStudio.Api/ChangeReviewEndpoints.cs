using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOrchestrator.CodeQuality;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace QualityStudio.Api;

/// <summary>Creates the reviewer one change-review call uses; replaced in tests to count model calls.</summary>
public interface IChangeReviewAgentFactory
{
    IReviewAgent Create(ChangeReviewVerdictRequest request);
}

/// <summary>The product's code-review route, or the explicitly requested CLI, model and thinking level.</summary>
public sealed class CodingAgentChangeReviewAgentFactory : IChangeReviewAgentFactory
{
    public IReviewAgent Create(ChangeReviewVerdictRequest request) =>
        string.IsNullOrWhiteSpace(request.Model) && string.IsNullOrWhiteSpace(request.ThinkingLevel)
            ? CodingAgentReviewAgent.CreateDefault(request.CliType ?? "codex")
            : new CodingAgentReviewAgent(request.CliType ?? "codex", request.Model, request.ThinkingLevel);
}

public sealed record ChangeReviewProvenanceRequest(string? Producer, string? CardKey, int? Round, string? AttemptId);

public sealed record ChangeReviewIngestRequest(JsonElement? Document, ChangeReviewProvenanceRequest? Provenance);

/// <summary>
/// The change-review verdict and its store. <c>POST change-review</c> answers from the store when
/// the key already holds a decisive verdict and pays for a model call only on a miss; every result
/// it computes is stored. Agent Studio pushes the verdicts its Review Plane computed with
/// <c>PUT /api/v1/repos/{id}/change-reviews</c>, and the change view reads a range with
/// <c>GET /api/v1/repos/{id}/change-reviews</c>. See docs/change-reviews.md.
/// </summary>
internal static partial class ChangeReviewEndpoints
{
    /// <summary>Whether a POST answered from the store (<c>stored</c>) or called the reviewer (<c>computed</c>).</summary>
    public const string SourceHeader = "Quality-Studio-Change-Review";

    public static void MapChangeReviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/change-review", Review).RequireRateLimiting("spend");
        app.MapPost("/api/repos/{repoId}/change-review", Review).RequireRateLimiting("spend");
        app.MapPost("/api/v1/repos/{repoId}/change-review", Review).RequireRateLimiting("spend");
        app.MapPut("/api/v1/repos/{repoId}/change-reviews", Ingest);
        app.MapGet("/api/v1/repos/{repoId}/change-reviews", Range);
    }

    private static async Task<IResult> Review(HttpContext context, ChangeReviewVerdictRequest request,
        RepositoryRegistry registry, ChangeReviewStore store, IChangeReviewAgentFactory agents, ApiSecurity security,
        IOptions<HttpJsonOptions> json, CancellationToken cancellationToken)
    {
        var (registration, root) = Resolve(context, registry);
        try
        {
            var prepared = await ChangeReviewVerdictService.PrepareAsync(registration.Id, root, request,
                registration.GlobalInputsDirectory, cancellationToken);
            var provenance = new ChangeReviewProvenance(ChangeReviewProvenance.QualityStudio,
                ClientId: security.Identity(context).Id);
            var (record, computed) = await store.GetOrComputeAsync(root, prepared.Key, provenance, async token =>
                JsonSerializer.SerializeToElement(
                    await new ChangeReviewVerdictService(agents.Create(request)).ReviewAsync(prepared, token),
                    json.Value.SerializerOptions), cancellationToken);
            context.Response.Headers[SourceHeader] = computed ? "computed" : "stored";
            return Results.Ok(record.Document);
        }
        catch (Exception exception) when (exception is ArgumentException or ChangeReviewException or JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid change-review request", detail: exception.Message);
        }
    }

    private static async Task<IResult> Ingest(HttpContext context, ChangeReviewIngestRequest request,
        RepositoryRegistry registry, ChangeReviewStore store, ApiSecurity security, CancellationToken cancellationToken)
    {
        var (registration, root) = Resolve(context, registry);
        if (request.Document is not { ValueKind: JsonValueKind.Object } document)
            return Unprocessable("Change-review document is invalid", "The request requires a 'document' object.");
        if (!ChangeReviewDocuments.IsSupportedVersion(document))
            return Unprocessable("Unsupported change-review document version",
                $"This host reads schema '{ChangeReviewVerdictService.SchemaId}' with schemaVersion 1 only.");
        var errors = ChangeReviewDocuments.Validate(document).ToList();
        if (errors.Count == 0 && document.GetProperty("repository").GetString() != registration.Id)
            errors.Add($"'repository' must be '{registration.Id}', the repository this route stores for.");
        var provenance = request.Provenance;
        if (provenance is null || !ChangeReviewProvenance.Producers.Contains(provenance.Producer))
            errors.Add("'provenance.producer' must be one of " + string.Join(", ", ChangeReviewProvenance.Producers) + ".");
        if (provenance?.CardKey is { } cardKey && !CardKey().IsMatch(cardKey))
            errors.Add("'provenance.cardKey' must be a task key such as AGT-3352.");
        if (provenance?.Round is < 1)
            errors.Add("'provenance.round' must be a positive integer.");
        if (provenance?.AttemptId is { } attemptId && (attemptId.Length is < 1 or > 128 || attemptId.Any(char.IsControl)))
            errors.Add("'provenance.attemptId' must be 1 to 128 printable characters.");
        if (errors.Count > 0)
            return Unprocessable("Change-review document is invalid", string.Join(" ", errors));

        var result = await store.PutAsync(root, ChangeReviewDocuments.KeyOf(document),
            new ChangeReviewProvenance(provenance!.Producer!, provenance.CardKey, provenance.Round, provenance.AttemptId,
                security.Identity(context).Id), document, cancellationToken);
        var body = new { outcome = result.Outcome, record = result.Record };
        return result.Outcome switch
        {
            ChangeReviewStoreOutcome.Stored => Results.Created(
                $"/api/v1/repos/{Uri.EscapeDataString(registration.Id)}/change-reviews" +
                $"?base={result.Record.Key.BaseSha}&head={result.Record.Key.HeadSha}", body),
            ChangeReviewStoreOutcome.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "A different decisive verdict is already stored for this key",
                detail: "A verdict is computed once per repository, range, policy and rule set; the stored one is kept.",
                extensions: new Dictionary<string, object?>
                {
                    ["keyDigest"] = result.Record.KeyDigest,
                    ["storedDocumentDigest"] = result.Record.DocumentDigest,
                }),
            _ => Results.Ok(body),
        };
    }

    private static async Task<IResult> Range(HttpContext context, string? @base, string? head, string? previous,
        RepositoryRegistry registry, ChangeReviewStore store, CancellationToken cancellationToken)
    {
        if (!ChangeReviewRange.IsCommitSha(@base) || !ChangeReviewRange.IsCommitSha(head) ||
            (previous is not null && !ChangeReviewRange.IsCommitSha(previous)))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid change-review range",
                detail: "base, head and previous must be full 40-character lowercase commit SHAs.");
        var (registration, root) = Resolve(context, registry);
        var records = store.List(root, registration.Id, @base!, head!);
        var basePresent = await ChangeReviewRange.CommitPresentAsync(root, @base!, cancellationToken);
        var headPresent = await ChangeReviewRange.CommitPresentAsync(root, head!, cancellationToken);
        bool? previousPresent = previous is null ? null
            : await ChangeReviewRange.CommitPresentAsync(root, previous, cancellationToken);
        var rangePresent = basePresent && headPresent;

        // The key POST change-review would use now; a stored verdict under another rule set stays valid
        // for its own key, and the view says which one it shows.
        ChangeReviewKey? currentKey = null;
        if (rangePresent)
        {
            try
            {
                currentKey = (await ChangeReviewVerdictService.PrepareAsync(registration.Id, root,
                    new ChangeReviewVerdictRequest(@base!, head!), registration.GlobalInputsDirectory,
                    cancellationToken)).Key;
            }
            catch (Exception exception) when (exception is ArgumentException or ChangeReviewException or JsonException)
            {
                currentKey = null;
            }
        }

        var anchors = records.SelectMany(record => Anchors(record.Document)).ToArray();
        var excerpts = rangePresent && anchors.Length > 0
            ? ChangeReviewRange.Excerpts(await ChangeReviewRange.DiffAsync(root, @base!, head!, cancellationToken), anchors)
            : new Dictionary<string, IReadOnlyList<ChangeReviewDiffLine>>();
        IReadOnlySet<string>? roundFingerprints = previous is not null && previousPresent == true && headPresent
            ? ChangeReviewRange.ChangedInRound(
                await ChangeReviewRange.DiffAsync(root, previous, head!, cancellationToken), anchors)
            : null;

        return Results.Ok(new
        {
            repository = registration.Id,
            baseSha = @base,
            headSha = head,
            commits = new { @base = basePresent, head = headPresent, previous = previousPresent },
            currentKey,
            // Every caller this host authenticates today (Local mode, bearer clients) may spend. Browser
            // sessions with a viewer role will not.
            permissions = new { runReview = true },
            records = records.Select(record => new
            {
                record.Key,
                record.KeyDigest,
                record.Provenance,
                record.RecordedAt,
                record.DocumentDigest,
                current = currentKey is not null && record.KeyDigest == currentKey.Digest,
                decisive = record.IsDecisive,
                record.Document,
            }),
            excerpts,
            roundOnly = previous is null ? null : new
            {
                previousHeadSha = previous,
                available = roundFingerprints is not null,
                fingerprints = roundFingerprints?.Order(StringComparer.Ordinal).ToArray() ?? [],
            },
        });
    }

    private static IEnumerable<ChangeReviewAnchor> Anchors(JsonElement document) =>
        document.TryGetProperty("findings", out var findings) && findings.ValueKind == JsonValueKind.Array
            ? findings.EnumerateArray().Select(finding => new ChangeReviewAnchor(
                finding.GetProperty("fingerprint").GetString()!, finding.GetProperty("path").GetString()!,
                finding.GetProperty("side").GetString()!, finding.GetProperty("line").GetInt32()))
            : [];

    private static (RepositoryRegistration Registration, string Root) Resolve(HttpContext context, RepositoryRegistry registry)
    {
        var id = context.Request.RouteValues.TryGetValue("repoId", out var value) ? value?.ToString() : null;
        var registration = registry.Get(id);
        return (registration, registry.Access(registration.Id).Root);
    }

    private static IResult Unprocessable(string title, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: title, detail: detail);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,15}-[0-9]{1,9}$")]
    private static partial Regex CardKey();
}
