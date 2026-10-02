using System.Security.Cryptography;
using System.Text;
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
    private const string IdentityItem = "QualityStudio.Api.Identity";
    private readonly ApiSecurityOptions options;
    private readonly HashSet<string> allowedOrigins;
    private readonly IReadOnlyList<(ApiClientIdentity Identity, byte[] CredentialHash)> clients;

    public ApiSecurity(IOptions<RepositoryOptions> configured)
    {
        options = configured.Value.Security;
        allowedOrigins = configured.Value.AllowedOrigins
            .Select(origin => TryOrigin(origin, out var parsed) ? parsed!.GetLeftPart(UriPartial.Authority) : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (options.Mode is not (ApiSecurityOptions.LocalMode or ApiSecurityOptions.HostedMode))
            throw new InvalidOperationException("QualityStudio:Security:Mode must be Local or Hosted.");
        if (options.MaxRequestBodyBytes is < 1024 or > 10 * 1024 * 1024)
            throw new InvalidOperationException("QualityStudio:Security:MaxRequestBodyBytes must be between 1 KiB and 10 MiB.");
        if (options.MaxConcurrentRequests is < 1 or > 1024)
            throw new InvalidOperationException("QualityStudio:Security:MaxConcurrentRequests must be between 1 and 1,024.");
        if (options.SpendRequestsPerMinute is < 1 or > 1000)
            throw new InvalidOperationException("QualityStudio:Security:SpendRequestsPerMinute must be between 1 and 1,000.");

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

    /// <summary>
    /// Loopback binding does not stop a browser on this machine from reaching the API through
    /// a foreign page or a rebinding hostname. Check the actual authority and browser origin
    /// before granting Local mode's registrar identity. Non-browser local clients remain supported.
    /// </summary>
    public bool IsLocalRequestTrusted(HttpContext context)
    {
        if (!IsLocal) return true;
        var request = context.Request;
        if (!TryOrigin($"{request.Scheme}://{request.Host.Value}", out var target)) return false;
        if (!options.AllowNonLoopbackLocalMode && !target!.IsLoopback) return false;

        var origins = request.Headers.Origin;
        if (origins.Count == 0)
            return !string.Equals(request.Headers["Sec-Fetch-Site"].ToString(), "cross-site",
                StringComparison.OrdinalIgnoreCase);
        if (origins.Count != 1 || !TryOrigin(origins[0], out var origin)) return false;
        var authority = origin!.GetLeftPart(UriPartial.Authority);
        return string.Equals(authority, target!.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || allowedOrigins.Contains(authority);
    }

    private static bool TryOrigin(string? value, out Uri? origin)
    {
        origin = null;
        return Uri.TryCreate(value, UriKind.Absolute, out origin)
            && (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)
            && origin.UserInfo.Length == 0
            && origin.AbsolutePath == "/"
            && origin.Query.Length == 0
            && origin.Fragment.Length == 0;
    }

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
}
