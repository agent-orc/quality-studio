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
    private readonly IReadOnlySet<string> allowedOrigins;
    private readonly IReadOnlyList<(ApiClientIdentity Identity, byte[] CredentialHash)> clients;

    public ApiSecurity(IOptions<RepositoryOptions> configured)
    {
        options = configured.Value.Security;
        allowedOrigins = configured.Value.AllowedOrigins
            .Select(NormalizeOrigin)
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

    public bool IsAllowedLocalOrigin(HttpContext context)
    {
        if (!IsLocal) return false;
        var supplied = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        string normalized;
        try
        {
            normalized = NormalizeOrigin(supplied);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        var requestOrigin = NormalizeOrigin($"{context.Request.Scheme}://{context.Request.Host}");
        return allowedOrigins.Contains(normalized) ||
               string.Equals(normalized, requestOrigin, StringComparison.OrdinalIgnoreCase);
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

    public static void ValidateLocalBindings(RepositoryOptions configured, IConfiguration configuration)
    {
        if (!string.Equals(configured.Security.Mode, ApiSecurityOptions.LocalMode, StringComparison.Ordinal)) return;
        var bindings = new List<string>();
        var urls = configuration["urls"];
        if (!string.IsNullOrWhiteSpace(urls))
            bindings.AddRange(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) ||
                 !string.IsNullOrWhiteSpace(configuration["https_ports"]))
            throw new InvalidOperationException(
                "Local security mode cannot use wildcard HTTP_PORTS or HTTPS_PORTS bindings; configure an explicit loopback URL.");
        bindings.AddRange(configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"])
            .Where(url => !string.IsNullOrWhiteSpace(url))!);
        foreach (var binding in bindings)
        {
            if (!Uri.TryCreate(binding, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
                throw new InvalidOperationException(
                    "Local security mode may bind only to localhost or a loopback IP address.");
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    private static string NormalizeOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            (origin.AbsolutePath != "/" && origin.AbsolutePath.Length > 0) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Allowed origins must be HTTP(S) origins without a path, query, or fragment.");
        return origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
