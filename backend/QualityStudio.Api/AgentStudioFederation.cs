using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

/// <summary>
/// <c>AgentStudio:Federation</c>: the Agent Studio whose signed hand-off this host accepts. Without
/// an <see cref="Issuer"/> the receiver does not exist and <c>/auth/agent-studio</c> answers 404.
/// </summary>
public sealed class AgentStudioFederationOptions
{
    public const string SectionName = "AgentStudio:Federation";

    /// <summary>The exact <c>iss</c> Agent Studio signs with, for example <c>https://robert.omgai.dev</c>.</summary>
    public string? Issuer { get; set; }

    /// <summary>The <c>aud</c> an assertion must name, normally this host's public origin.</summary>
    public string? Audience { get; set; }

    /// <summary>Where the issuer publishes its key set, for example <c>https://robert.omgai.dev/.well-known/agent-studio/jwks.json</c>.</summary>
    public string? JwksUrl { get; set; }

    /// <summary>Pinned Ed25519 public keys, used instead of or next to <see cref="JwksUrl"/>.</summary>
    public List<FederationPinnedKey> Keys { get; set; } = [];

    /// <summary>The Studio origin users come from: the hand-off's <c>Origin</c> and the sign-in redirect target.</summary>
    public string? StudioPublicUrl { get; set; }

    /// <summary>Bearer client ids allowed to revoke sessions. Defaults to <c>agent-studio</c>.</summary>
    public string[]? ServiceClients { get; set; }
}

public sealed class FederationPinnedKey
{
    public string? Kid { get; set; }

    /// <summary>The 32-byte public key, base64url as in a JWK's <c>x</c>.</summary>
    public string? X { get; set; }
}

public enum FederatedRole
{
    Viewer = 0,
    Operator = 1,
    Owner = 2,
}

public sealed record HandoffClaims(
    string Subject,
    string SessionId,
    FederatedRole Role,
    IReadOnlySet<string> Repositories,
    string Target,
    string Jti,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Why a hand-off was refused, phrased for the person whose browser posted it. Never echoes the assertion.</summary>
public sealed record HandoffOutcome(HandoffClaims? Claims, int StatusCode, string Reason)
{
    public static HandoffOutcome Accepted(HandoffClaims claims) => new(claims, StatusCodes.Status303SeeOther, "Signed in");

    public static HandoffOutcome Refused(int statusCode, string reason) => new(null, statusCode, reason);
}

/// <summary>
/// The hand-off receiver's configuration and verification: an Ed25519-signed JWS from the configured
/// Agent Studio, bound to this audience, at most 60 seconds valid with 30 seconds of clock skew, of a
/// known contract version, posted from the Studio origin, and used once.
/// </summary>
public sealed class AgentStudioFederation
{
    public const int SupportedVersion = 1;
    public const string SignInPath = "/auth/agent-studio";
    public const string StudioOpenPath = "/api/integrations/quality-studio/open";
    /// <summary>The <see cref="RepositoryExternalRef.System"/> naming an Agent Studio project, as in <c>agent-studio:PROJ-002</c>.</summary>
    public const string ExternalRefSystem = "agent-studio";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private const int MaximumAssertionCharacters = 8 * 1024;
    private const int MaximumRepositories = 512;

    private readonly FederationKeySet? keys;
    private readonly FederationReplayStore? replays;
    private readonly FederatedSessionStore sessions;
    private readonly TimeProvider time;

    public AgentStudioFederation(
        IOptions<AgentStudioFederationOptions> configured,
        IOptions<RepositoryOptions> repositoryOptions,
        HttpClient httpClient,
        FederatedSessionStore sessions,
        TimeProvider time,
        ILogger<AgentStudioFederation> logger)
    {
        this.sessions = sessions;
        this.time = time;
        var options = configured.Value;
        Enabled = !string.IsNullOrWhiteSpace(options.Issuer);
        if (!Enabled) return;

        if (string.Equals(repositoryOptions.Value.Security.Mode, ApiSecurityOptions.LocalMode, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "AgentStudio:Federation requires QualityStudio:Security:Mode=Hosted; Local mode authenticates nobody.");
        Issuer = options.Issuer!.Trim();
        Audience = string.IsNullOrWhiteSpace(options.Audience)
            ? throw new InvalidOperationException("AgentStudio:Federation:Audience is required when an Issuer is configured.")
            : options.Audience.Trim();
        if (!TryWebOrigin(options.StudioPublicUrl, out var studioUrl))
            throw new InvalidOperationException(
                "AgentStudio:Federation:StudioPublicUrl must be an absolute http(s) URL when an Issuer is configured.");
        StudioPublicUrl = studioUrl!.GetLeftPart(UriPartial.Path).TrimEnd('/');
        StudioOrigin = studioUrl.GetLeftPart(UriPartial.Authority);
        ServiceClients = (options.ServiceClients is { Length: > 0 } clients ? clients : ["agent-studio"])
            .Where(client => !string.IsNullOrWhiteSpace(client))
            .Select(client => client.Trim())
            .ToHashSet(StringComparer.Ordinal);
        keys = new FederationKeySet(options, httpClient, time, logger);
        replays = new FederationReplayStore(time);
    }

    public bool Enabled { get; }
    public string Issuer { get; } = string.Empty;
    public string Audience { get; } = string.Empty;
    public string StudioPublicUrl { get; } = string.Empty;
    public string StudioOrigin { get; } = string.Empty;
    public IReadOnlySet<string> ServiceClients { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Where a browser without a session starts: Studio's open route, which checks the Studio session
    /// and answers with a fresh hand-off. <paramref name="returnPath"/> must already be relative.
    /// </summary>
    public string SignInUrl(string? project, string returnPath)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(project)) query.Add("project=" + Uri.EscapeDataString(project));
        query.Add("return=" + Uri.EscapeDataString(IsRelativeTarget(returnPath) ? returnPath : "/"));
        return StudioPublicUrl + StudioOpenPath + "?" + string.Join('&', query);
    }

    public async Task<HandoffOutcome> VerifyAsync(string? handoff, string? origin, CancellationToken cancellationToken)
    {
        if (!Enabled) return HandoffOutcome.Refused(StatusCodes.Status404NotFound, "Not found");
        if (!string.Equals(origin, StudioOrigin, StringComparison.OrdinalIgnoreCase))
            return HandoffOutcome.Refused(StatusCodes.Status403Forbidden,
                "The sign-in did not come from the configured Agent Studio.");
        if (string.IsNullOrWhiteSpace(handoff) || handoff.Length > MaximumAssertionCharacters)
            return HandoffOutcome.Refused(StatusCodes.Status400BadRequest, "The sign-in carried no hand-off.");

        var parts = handoff.Split('.');
        if (parts.Length != 3 || !TryDecode(parts[0], out var headerBytes) || !TryDecode(parts[1], out var payloadBytes) ||
            !TryDecode(parts[2], out var signature) || signature.Length != Ed25519.SignatureBytes)
            return Invalid();

        string? kid;
        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            var root = header.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String ||
                !string.Equals(alg.GetString(), "EdDSA", StringComparison.Ordinal) ||
                root.TryGetProperty("crit", out _))
                return Invalid();
            kid = root.TryGetProperty("kid", out var kidValue) && kidValue.ValueKind == JsonValueKind.String
                ? kidValue.GetString()
                : null;
        }
        catch (JsonException)
        {
            return Invalid();
        }

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var candidates = await keys!.CandidatesAsync(kid, cancellationToken);
        if (candidates is null)
            return HandoffOutcome.Refused(StatusCodes.Status503ServiceUnavailable,
                "Agent Studio's signing keys could not be read. Try again from Agent Studio.");
        if (!candidates.Any(key => Ed25519.Verify(key, signingInput, signature))) return Invalid();

        HandoffOutcome outcome;
        try
        {
            using var payload = JsonDocument.Parse(payloadBytes);
            outcome = ReadClaims(payload.RootElement);
        }
        catch (JsonException)
        {
            return Invalid();
        }
        if (outcome.Claims is not { } claims) return outcome;
        if (sessions.IsRevoked(claims.Subject, claims.SessionId, claims.IssuedAt))
            return HandoffOutcome.Refused(StatusCodes.Status401Unauthorized,
                "This Agent Studio session has ended. Sign in to Agent Studio again.");
        if (!await replays!.TryConsumeAsync(claims.Jti, claims.ExpiresAt + ClockSkew, cancellationToken))
            return HandoffOutcome.Refused(StatusCodes.Status401Unauthorized,
                "This sign-in link was already used. Open Quality Studio from Agent Studio again.");
        return outcome;
    }

    private HandoffOutcome ReadClaims(JsonElement claims)
    {
        if (claims.ValueKind != JsonValueKind.Object) return Invalid();
        if (String(claims, "iss") != Issuer) return Invalid();
        if (!AudienceMatches(claims)) return Invalid();
        if (!claims.TryGetProperty("ver", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var ver) || ver != SupportedVersion)
            return HandoffOutcome.Refused(StatusCodes.Status422UnprocessableEntity,
                $"This Agent Studio sends a hand-off version this Quality Studio does not support (supported: {SupportedVersion}).");

        if (Seconds(claims, "iat") is not { } issuedAt || Seconds(claims, "exp") is not { } expiresAt) return Invalid();
        var now = time.GetUtcNow();
        if (expiresAt <= issuedAt || expiresAt - issuedAt > MaximumLifetime || issuedAt > now + ClockSkew ||
            now > expiresAt + ClockSkew ||
            (Seconds(claims, "nbf") is { } notBefore && notBefore > now + ClockSkew))
            return HandoffOutcome.Refused(StatusCodes.Status401Unauthorized,
                "This sign-in link has expired. Open Quality Studio from Agent Studio again.");

        var jti = String(claims, "jti");
        var subject = String(claims, "sub");
        var sessionId = String(claims, "sid");
        if (jti is not { Length: >= 1 and <= 128 } || subject is not { Length: >= 1 and <= 256 } ||
            sessionId is not { Length: >= 1 and <= 256 })
            return Invalid();
        FederatedRole role;
        switch (String(claims, "role"))
        {
            case "viewer": role = FederatedRole.Viewer; break;
            case "operator": role = FederatedRole.Operator; break;
            case "owner": role = FederatedRole.Owner; break;
            default: return Invalid();
        }
        if (!claims.TryGetProperty("repos", out var repos) || repos.ValueKind != JsonValueKind.Array ||
            repos.GetArrayLength() > MaximumRepositories)
            return Invalid();
        var repositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repository in repos.EnumerateArray())
        {
            if (repository.ValueKind != JsonValueKind.String || !IsRepositoryId(repository.GetString())) return Invalid();
            repositories.Add(repository.GetString()!.ToLowerInvariant());
        }

        var target = claims.TryGetProperty("target", out var targetValue) ? targetValue : default;
        var resolvedTarget = target.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => "/",
            JsonValueKind.String => target.GetString()!,
            _ => null,
        };
        if (resolvedTarget is null || !IsRelativeTarget(resolvedTarget))
            return HandoffOutcome.Refused(StatusCodes.Status400BadRequest,
                "The sign-in named a destination outside Quality Studio.");

        return HandoffOutcome.Accepted(new HandoffClaims(subject, sessionId, role, repositories, resolvedTarget, jti,
            issuedAt, expiresAt));
    }

    private bool AudienceMatches(JsonElement claims)
    {
        if (!claims.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == Audience,
            JsonValueKind.Array => audience.EnumerateArray()
                .Any(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() == Audience),
            _ => false,
        };
    }

    /// <summary>
    /// A same-host path: one leading slash, never <c>//</c> or a backslash (both read as another
    /// host by browsers), no scheme and no control characters.
    /// </summary>
    public static bool IsRelativeTarget(string? target) =>
        target is { Length: >= 1 and <= 2048 }
        && target[0] == '/'
        && !target.StartsWith("//", StringComparison.Ordinal)
        && !target.Contains('\\')
        && !target.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
        && Uri.TryCreate(target, UriKind.Relative, out _);

    internal static bool IsRepositoryId(string? id) =>
        id is { Length: >= 1 and <= 64 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    internal static bool TryWebOrigin(string? value, out Uri? uri)
    {
        uri = null;
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    internal static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0 || !Base64Url.IsValid(value)) return false;
        try
        {
            bytes = Base64Url.DecodeFromChars(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? String(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Seconds(JsonElement claims, string name) =>
        claims.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    private static HandoffOutcome Invalid() =>
        HandoffOutcome.Refused(StatusCodes.Status401Unauthorized,
            "The sign-in from Agent Studio could not be verified. Open Quality Studio from Agent Studio again.");
}

/// <summary>
/// The issuer's Ed25519 keys: pinned in configuration, fetched from its JWKS, or both. A fetched set
/// is reused for ten minutes and refreshed early, at most every 30 seconds, when an assertion names a
/// key id it does not hold yet, so a key rotation needs no restart.
/// </summary>
internal sealed class FederationKeySet
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshFloor = TimeSpan.FromSeconds(30);
    private readonly IReadOnlyList<(string? Kid, byte[] Key)> pinned;
    private readonly Uri? jwksUrl;
    private readonly HttpClient httpClient;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<(string? Kid, byte[] Key)> fetched = [];
    private DateTimeOffset fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset attemptedAt = DateTimeOffset.MinValue;

    public FederationKeySet(AgentStudioFederationOptions options, HttpClient httpClient, TimeProvider time, ILogger logger)
    {
        this.httpClient = httpClient;
        this.time = time;
        this.logger = logger;
        pinned = options.Keys.Select(key =>
                AgentStudioFederation.TryDecode(key.X?.Trim() ?? string.Empty, out var bytes) && bytes.Length == Ed25519.PublicKeyBytes
                    ? (string.IsNullOrWhiteSpace(key.Kid) ? null : key.Kid.Trim(), bytes)
                    : throw new InvalidOperationException(
                        "Each AgentStudio:Federation:Keys entry needs X, a 32-byte Ed25519 public key in base64url."))
            .ToArray();
        if (!string.IsNullOrWhiteSpace(options.JwksUrl))
        {
            if (!AgentStudioFederation.TryWebOrigin(options.JwksUrl, out var parsed))
                throw new InvalidOperationException("AgentStudio:Federation:JwksUrl must be an absolute http(s) URL.");
            jwksUrl = parsed;
        }
        if (pinned.Count == 0 && jwksUrl is null)
            throw new InvalidOperationException("AgentStudio:Federation needs a JwksUrl or at least one pinned key.");
    }

    /// <summary>The keys an assertion with <paramref name="kid"/> may be signed with, or null when none could be read.</summary>
    public async Task<IReadOnlyList<byte[]>?> CandidatesAsync(string? kid, CancellationToken cancellationToken)
    {
        if (jwksUrl is not null)
        {
            var now = time.GetUtcNow();
            var stale = now - fetchedAt > CacheLifetime;
            var unknownKid = kid is not null && !fetched.Any(key => key.Kid == kid) && !pinned.Any(key => key.Kid == kid);
            if ((stale || unknownKid) && now - attemptedAt > RefreshFloor) await RefreshAsync(cancellationToken);
        }

        var all = pinned.Concat(fetched).ToArray();
        if (all.Length == 0) return null;
        var matching = kid is null ? all : all.Where(key => key.Kid is null || key.Kid == kid).ToArray();
        return matching.Select(key => key.Key).ToArray();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (now - attemptedAt <= RefreshFloor) return;
            attemptedAt = now;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await httpClient.GetAsync(jwksUrl, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            fetched = ParseKeySet(document.RootElement);
            fetchedAt = now;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException
                                              or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            // The last good set stays in use; a pinned key keeps working regardless.
            logger.LogWarning(new EventId(1700, "FederationKeySetUnavailable"), exception,
                "The Agent Studio key set at {JwksUrl} could not be read", jwksUrl);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static IReadOnlyList<(string? Kid, byte[] Key)> ParseKeySet(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("keys", out var keys) ||
            keys.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The key set has no keys array.");
        var parsed = new List<(string?, byte[])>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object) continue;
            if (Text(key, "kty") != "OKP" || Text(key, "crv") != "Ed25519") continue;
            if (Text(key, "use") is { } use && use != "sig") continue;
            if (Text(key, "alg") is { } alg && alg != "EdDSA") continue;
            if (!AgentStudioFederation.TryDecode(Text(key, "x") ?? string.Empty, out var bytes) ||
                bytes.Length != Ed25519.PublicKeyBytes)
                continue;
            parsed.Add((Text(key, "kid"), bytes));
        }
        return parsed;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
