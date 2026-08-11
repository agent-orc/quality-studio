using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public sealed record ApiClientIdentity(string Id, IReadOnlySet<string> Repositories, bool CanRegisterRepositories)
{
    public bool CanAccess(string repositoryId) =>
        Repositories.Contains("*") || Repositories.Contains(repositoryId);
}

public sealed class ApiSecurity
{
    public const string ClientIdHeader = "X-Client-Id";
    public const string AntiCsrfHeader = "X-Quality-Studio-CSRF";
    public const string AntiCsrfCookie = "quality-studio-csrf";
    private const string IdentityItem = "QualityStudio.Api.Identity";
    private readonly ApiSecurityOptions options;
    private readonly HashSet<string> allowedOrigins;
    private readonly byte[] antiCsrfKey = RandomNumberGenerator.GetBytes(32);
    private readonly IReadOnlyList<(ApiClientIdentity Identity, byte[] CredentialHash)> clients;

    public ApiSecurity(IOptions<RepositoryOptions> configured)
    {
        options = configured.Value.Security;
        if (options.Mode is not (ApiSecurityOptions.LocalMode or ApiSecurityOptions.HostedMode))
            throw new InvalidOperationException("QualityStudio:Security:Mode must be Local or Hosted.");
        if (options.MaxRequestBodyBytes is < 1024 or > 10 * 1024 * 1024)
            throw new InvalidOperationException("QualityStudio:Security:MaxRequestBodyBytes must be between 1 KiB and 10 MiB.");
        if (options.MaxConcurrentRequests is < 1 or > 1024)
            throw new InvalidOperationException("QualityStudio:Security:MaxConcurrentRequests must be between 1 and 1,024.");
        if (options.SpendRequestsPerMinute is < 1 or > 1000)
            throw new InvalidOperationException("QualityStudio:Security:SpendRequestsPerMinute must be between 1 and 1,000.");
        if (options.LocalAntiCsrfLifetimeMinutes is < 1 or > 1440)
            throw new InvalidOperationException(
                "QualityStudio:Security:LocalAntiCsrfLifetimeMinutes must be between 1 and 1,440.");
        allowedOrigins = configured.Value.AllowedOrigins
            .Select(NormalizeOrigin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedOrigins.Count == 0)
            throw new InvalidOperationException("QualityStudio:AllowedOrigins must contain at least one origin.");

        if (IsLocal)
        {
            clients = [];
            return;
        }

        if (options.Clients.Count == 0)
            throw new InvalidOperationException("Hosted mode requires at least one configured API client.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<(ApiClientIdentity, byte[])>();
        foreach (var client in options.Clients)
        {
            var id = client.Id.Trim();
            if (id.Length is < 1 or > 128 || !ids.Add(id))
                throw new InvalidOperationException("Hosted API client ids must be non-empty and unique.");
            if (client.CredentialSha256.Length != 64 ||
                !client.CredentialSha256.All(character => Uri.IsHexDigit(character)) ||
                !hashes.Add(client.CredentialSha256))
                throw new InvalidOperationException("Each hosted API client requires a unique SHA-256 credential hash.");
            var repositories = client.Repositories
                .Where(repository => !string.IsNullOrWhiteSpace(repository))
                .Select(repository => repository.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (repositories.Count == 0)
                throw new InvalidOperationException("Each hosted API client must be registered for at least one repository.");
            if (client.CanRegisterRepositories && !repositories.Contains("*"))
                throw new InvalidOperationException("Repository registrars must explicitly have wildcard repository access.");
            validated.Add((new ApiClientIdentity(id, repositories, client.CanRegisterRepositories),
                Convert.FromHexString(client.CredentialSha256)));
        }
        clients = validated;
    }

    public bool IsLocal => string.Equals(options.Mode, ApiSecurityOptions.LocalMode, StringComparison.Ordinal);
    public bool RequireHttps => !IsLocal && options.RequireHttps;
    public long MaxRequestBodyBytes => options.MaxRequestBodyBytes;
    public int MaxConcurrentRequests => options.MaxConcurrentRequests;
    public int SpendRequestsPerMinute => options.SpendRequestsPerMinute;

    public ApiClientIdentity? Authenticate(HttpContext context)
    {
        if (IsLocal)
            return new ApiClientIdentity("local-development", new HashSet<string>(["*"], StringComparer.Ordinal), true);

        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var credential = authorization[prefix.Length..].Trim();
        if (credential.Length == 0) return null;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        ApiClientIdentity? matched = null;
        foreach (var client in clients)
        {
            if (CryptographicOperations.FixedTimeEquals(suppliedHash, client.CredentialHash))
                matched = client.Identity;
        }
        return matched;
    }

    public void SetIdentity(HttpContext context, ApiClientIdentity identity) => context.Items[IdentityItem] = identity;

    public ApiClientIdentity Identity(HttpContext context) =>
        context.Items.TryGetValue(IdentityItem, out var value) && value is ApiClientIdentity identity
            ? identity
            : throw new InvalidOperationException("The API security middleware did not establish an identity.");

    public bool IsMutationClientHeaderValid(HttpContext context, ApiClientIdentity identity) =>
        IsLocal || string.Equals(context.Request.Headers[ClientIdHeader].ToString(), identity.Id, StringComparison.Ordinal);

    public (string HeaderName, string Token, DateTimeOffset ExpiresAt) CreateLocalAntiCsrfSession(HttpContext context)
    {
        if (!IsLocal) throw new InvalidOperationException("Local anti-CSRF sessions are unavailable in Hosted mode.");
        var expires = DateTimeOffset.UtcNow.AddMinutes(options.LocalAntiCsrfLifetimeMinutes);
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(24));
        var payload = $"{nonce}.{expires.ToUnixTimeSeconds()}";
        var signature = Base64Url(HMACSHA256.HashData(antiCsrfKey, Encoding.UTF8.GetBytes(payload)));
        var token = $"{payload}.{signature}";
        context.Response.Cookies.Append(AntiCsrfCookie, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Expires = expires,
            Path = "/",
        });
        return (AntiCsrfHeader, token, expires);
    }

    public bool IsLocalMutationValid(HttpContext context)
    {
        if (!IsLocal) return true;
        if (!context.Request.Headers.TryGetValue("Origin", out var origins) || origins.Count != 1)
            return false;
        string origin;
        try
        {
            origin = NormalizeOrigin(origins[0]!);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (!allowedOrigins.Contains(origin)) return false;
        if (!context.Request.Cookies.TryGetValue(AntiCsrfCookie, out var cookieToken)) return false;
        var headerToken = context.Request.Headers[AntiCsrfHeader].ToString();
        if (headerToken.Length == 0 || !FixedTimeEquals(cookieToken, headerToken)) return false;
        return ValidateAntiCsrfToken(headerToken);
    }

    public void ValidateLocalBindings(IConfiguration configuration)
    {
        if (!IsLocal) return;
        var bindings = new List<string>();
        if (configuration["urls"] is { Length: > 0 } urls)
            bindings.AddRange(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url) bindings.Add(url);
        }
        foreach (var binding in bindings)
        {
            if (!Uri.TryCreate(binding, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
            {
                throw new InvalidOperationException(
                    "QualityStudio Local security mode may bind only to localhost or a loopback IP address.");
            }
        }
    }

    private bool ValidateAntiCsrfToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 ||
            !long.TryParse(parts[1], out var expiresAt) ||
            expiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;
        var payload = $"{parts[0]}.{parts[1]}";
        var expected = Base64Url(HMACSHA256.HashData(antiCsrfKey, Encoding.UTF8.GetBytes(payload)));
        return FixedTimeEquals(expected, parts[2]);
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Configured origins must be HTTP(S) origins without a path.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
}
