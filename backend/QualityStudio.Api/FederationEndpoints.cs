using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.Extensions;

namespace QualityStudio.Api;

public sealed record FederationRevokeRequest(string? Sid, string? Sub);

/// <summary>
/// The Agent Studio integration contract <c>agent-studio-integration/1</c> on this host: the
/// hand-off receiver, the caller's own session, session revocation and the capabilities document.
/// </summary>
internal static class FederationEndpoints
{
    public const string Contract = IntegrationCapabilities.Contract;
    public const string CapabilitiesSchemaId =
        "https://agent-orchestrator.dev/quality/schemas/integration-capabilities.v1.schema.json";

    public static void MapFederationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(AgentStudioFederation.SignInPath, SignIn);
        app.MapGet("/api/v1/session", Session);
        app.MapPost("/api/v1/federation/revoke", Revoke);
        app.MapGet("/api/v1/capabilities", Capabilities);
    }

    /// <summary>
    /// Verifies the posted hand-off, opens a session and sends the browser on with 303. The form body
    /// is read once here and never logged; a refusal names the reason, not the assertion.
    /// </summary>
    private static async Task<IResult> SignIn(HttpContext context, AgentStudioFederation federation,
        FederatedSessionStore sessions, ApiSecurity security, ILogger<AgentStudioFederation> logger,
        CancellationToken cancellationToken)
    {
        if (!federation.Enabled) return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (security.RequireHttps && !context.Request.IsHttps)
            return Refusal(federation, StatusCodes.Status400BadRequest, "HTTPS is required.");

        string? handoff = null;
        if (context.Request.HasFormContentType)
        {
            try
            {
                var form = await context.Request.ReadFormAsync(cancellationToken);
                if (form.TryGetValue("handoff", out var values) && values.Count == 1) handoff = values[0];
            }
            catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException or IOException)
            {
                handoff = null;
            }
        }

        var origins = context.Request.Headers.Origin;
        HandoffOutcome outcome;
        try
        {
            outcome = await federation.VerifyAsync(handoff, origins.Count == 1 ? origins[0] : null, cancellationToken);
        }
        catch (FederationStoreUnavailableException exception)
        {
            logger.LogError(new EventId(1711, "FederationReplayLedgerUnavailable"), exception,
                "Agent Studio sign-in refused because the replay ledger is unreadable");
            return Refusal(federation, StatusCodes.Status503ServiceUnavailable,
                "Quality Studio cannot record sign-ins right now. Try again later.");
        }

        if (outcome.Claims is not { } claims)
        {
            logger.LogWarning(new EventId(1710, "FederationSignInRefused"),
                "Agent Studio sign-in refused with {StatusCode}: {Reason}", outcome.StatusCode, outcome.Reason);
            return Refusal(federation, outcome.StatusCode, outcome.Reason);
        }

        var (cookie, session) = sessions.Create(claims);
        context.Response.Cookies.Append(FederatedSessionStore.CookieName, cookie, SessionCookieOptions(httpOnly: true));
        // Readable by this origin's pages, which repeat it as X-CSRF-Token; host-only through the
        // __Host- prefix, so a sibling host can neither read nor plant it.
        context.Response.Cookies.Append(FederatedSessionStore.CsrfCookieName, session.CsrfToken,
            SessionCookieOptions(httpOnly: false));
        logger.LogInformation(new EventId(1712, "FederationSignIn"),
            "Agent Studio sign-in for {Subject} as {Role} with {RepositoryCount} repositories",
            session.Subject, session.Role, session.Repositories.Count);
        context.Response.Headers.Location = claims.Target;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static CookieOptions SessionCookieOptions(bool httpOnly) => new()
    {
        HttpOnly = httpOnly,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = FederatedSessionStore.AbsoluteTimeout,
        IsEssential = true,
    };

    /// <summary>Who the caller is to this host. The browser reads its CSRF token and client id here.</summary>
    private static IResult Session(HttpContext context, ApiSecurity security, AgentStudioFederation federation)
    {
        var identity = security.Identity(context);
        var session = identity.Session;
        return Results.Ok(new
        {
            mode = security.IsLocal ? "local" : "hosted",
            kind = security.IsLocal ? "local" : session is not null ? "session" : "client",
            clientId = security.IsLocal || session is not null ? null : identity.Id,
            subject = session?.Subject,
            role = session?.Role.ToString().ToLowerInvariant(),
            repositories = identity.Repositories.Order(StringComparer.Ordinal).ToArray(),
            csrfToken = session?.CsrfToken,
            expiresAt = session is null ? (DateTimeOffset?)null : session.CreatedAt + FederatedSessionStore.AbsoluteTimeout,
            federation = new
            {
                enabled = federation.Enabled,
                studioPublicUrl = federation.Enabled ? federation.StudioPublicUrl : null,
            },
        });
    }

    private static IResult Revoke(FederationRevokeRequest request, HttpContext context, ApiSecurity security,
        AgentStudioFederation federation, FederatedSessionStore sessions, ILogger<AgentStudioFederation> logger)
    {
        if (!federation.Enabled) return Results.NotFound();
        var identity = security.Identity(context);
        if (identity.Session is not null || !federation.ServiceClients.Contains(identity.Id))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "Session revocation is reserved for the Agent Studio service client");
        var sid = string.IsNullOrWhiteSpace(request.Sid) ? null : request.Sid.Trim();
        var sub = string.IsNullOrWhiteSpace(request.Sub) ? null : request.Sub.Trim();
        if (sid is null && sub is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Name a sid or a sub to revoke");
        var ended = sessions.Revoke(sid, sub);
        logger.LogInformation(new EventId(1713, "FederationRevoked"),
            "Agent Studio revoked {Count} sessions (subject {Subject}, by session id {BySessionId})",
            ended, sub, sid is not null);
        return Results.Ok(new { revoked = ended });
    }

    private static IResult Capabilities(ApiSecurity security, AgentStudioFederation federation) =>
        Results.Ok(CapabilitiesDocument(security, federation));

    internal static object CapabilitiesDocument(ApiSecurity security, AgentStudioFederation federation)
    {
        var providers = SpendProviders.Available();
        var features = new List<string> { "deep-link/1", "external-ref", IntegrationCapabilities.ChangeReviewStore };
        if (federation.Enabled) features.Add("federation");
        return new
        {
            schema = CapabilitiesSchemaId,
            schemaVersion = 1,
            product = "quality-studio",
            version = ProductVersion(),
            productVersion = ProductVersion(),
            contract = Contract,
            contracts = new[] { Contract },
            features,
            mode = security.IsLocal ? "local" : "hosted",
            federation = new
            {
                enabled = federation.Enabled,
                signInPath = federation.Enabled ? AgentStudioFederation.SignInPath : null,
                versions = federation.Enabled ? new[] { AgentStudioFederation.SupportedVersion } : [],
                audience = federation.Enabled ? federation.Audience : null,
            },
            spend = new
            {
                providerAvailable = providers.Count > 0,
                providers,
            },
        };
    }

    private static string ProductVersion()
    {
        var version = typeof(FederationEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    /// <summary>
    /// The page a refused hand-off shows: the reason and a way back to Agent Studio. It is a
    /// document rather than problem JSON because the browser navigated here by submitting a form.
    /// </summary>
    private static IResult Refusal(AgentStudioFederation federation, int statusCode, string reason)
    {
        var encoder = HtmlEncoder.Default;
        var studio = federation.Enabled
            ? $"<p><a href=\"{encoder.Encode(federation.StudioPublicUrl)}\">Back to Agent Studio</a></p>"
            : string.Empty;
        var html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Quality Studio sign-in</title></head>" +
                   $"<body><h1>Sign-in from Agent Studio failed</h1><p>{encoder.Encode(reason)}</p>{studio}</body></html>";
        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }

    /// <summary>
    /// For a page load without a session on a federated host: where to send the browser so Agent
    /// Studio can sign it in and return it to the same place. Null when the page may load.
    /// </summary>
    internal static string? SignInRedirect(HttpContext context)
    {
        var federation = context.RequestServices.GetService<AgentStudioFederation>();
        if (federation is not { Enabled: true }) return null;
        var sessions = context.RequestServices.GetRequiredService<FederatedSessionStore>();
        if (sessions.Find(context.Request.Cookies[FederatedSessionStore.CookieName]) is not null) return null;
        // Until registrations carry an Agent Studio externalRef (QSI-3), the repository id is the
        // project reference: D2 makes it the lowercase project id.
        var repository = context.Request.Query["repo"].ToString();
        var project = AgentStudioFederation.IsRepositoryId(repository) ? repository.ToLowerInvariant() : null;
        return federation.SignInUrl(project, context.Request.GetEncodedPathAndQuery());
    }
}

/// <summary>
/// Which hand-off role an API route needs. Viewers read; operators also change finding states, run
/// reviews, scans and handover; owners also change registrations and what the repository
/// configures (rule pool, guidelines, scope rules, the ignore list). Applies to session callers only:
/// bearer clients keep their configured rights.
/// </summary>
internal static class FederatedAccessPolicy
{
    /// <summary>Unsafe-method routes that compute and return something without changing state.</summary>
    private static readonly HashSet<string> ReadOnlyPosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "/review/estimate", "/guidelines/impact", "/scope/rules/preview", "/rules/custom/validate",
    };

    private static readonly HashSet<string> OperatorRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/findings/state", "/threads", "/review", "/change-review", "/review/runs/{id}", "/review/runs/{id}/pause",
        "/review/runs/{id}/resume", "/review/runs/{id}/pin", "/handover", "/sensors/{id}/scan",
        "/security/attack-coverage/judgements",
    };

    public static FederatedRole RequiredRole(string method, string? canonicalRoute)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
            return FederatedRole.Viewer;
        var suffix = Suffix(canonicalRoute);
        if (suffix is null) return FederatedRole.Owner;
        if (HttpMethods.IsPost(method) && ReadOnlyPosts.Contains(suffix)) return FederatedRole.Viewer;
        return OperatorRoutes.Contains(suffix) ? FederatedRole.Operator : FederatedRole.Owner;
    }

    private static string? Suffix(string? route)
    {
        const string scoped = "/api/repos/{repoId}";
        if (route is null) return null;
        if (route.StartsWith(scoped + "/", StringComparison.OrdinalIgnoreCase)) return route[scoped.Length..];
        return route.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? route["/api".Length..] : null;
    }
}

internal static class ApiRoutes
{
    private const string VersionPrefix = "/api/v1/";

    /// <summary>The route pattern with an <c>/api/v1</c> alias folded onto its <c>/api</c> original, so one policy covers both.</summary>
    public static string? Canonical(string? routePattern) =>
        routePattern is not null && routePattern.StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase)
            ? "/api/" + routePattern[VersionPrefix.Length..]
            : routePattern;
}

/// <summary>Whether this host can spend on a model at all: a reviewer CLI it knows is on the PATH.</summary>
internal static class SpendProviders
{
    private static readonly string[] Executables = ["codex", "claude", "gemini"];

    public static IReadOnlyList<string> Available()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [string.Empty];
        return Executables.Where(executable => directories.Any(directory =>
                extensions.Any(extension => File.Exists(Path.Combine(directory, executable + extension)))))
            .ToArray();
    }
}
