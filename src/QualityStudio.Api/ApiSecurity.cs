using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

public static class ApiRoles
{
    public const string Read = "read";
    public const string RepositoryAdmin = "repository-admin";
    public const string ReviewSpend = "review-spend";
    public const string SensorExecute = "sensor-execute";
    public const string StateMutate = "state-mutate";
    public const string Handover = "handover";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Read, RepositoryAdmin, ReviewSpend, SensorExecute, StateMutate, Handover],
        StringComparer.Ordinal);
}

public sealed record ApiClientIdentity(
    string Id,
    string KeyId,
    string Audience,
    DateTimeOffset ExpiresAt,
    IReadOnlySet<string> Repositories,
    IReadOnlySet<string> Roles)
{
    public bool CanAccess(string repositoryId) =>
        Repositories.Contains("*") || Repositories.Contains(repositoryId);

    public bool HasRole(string role) => Roles.Contains(role);

    public bool CanRegisterRepositories => HasRole(ApiRoles.RepositoryAdmin);
}

public sealed record ApiAuthenticationDecision(ApiClientIdentity? Identity, string? KeyId, string Reason);

/// <summary>Explicit authorization metadata required on every API endpoint.</summary>
public sealed record ApiRoleMetadata(string Role);

public sealed class ApiSecurity
{
    public const string ClientIdHeader = "X-Client-Id";
    private const string IdentityItem = "QualityStudio.Api.Identity";
    private const int MaxRevocationFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions RevocationJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ApiSecurityOptions options;
    private readonly IReadOnlySet<string> allowedOrigins;
    private readonly IReadOnlyList<ConfiguredClient> clients;
    private readonly string? revocationFile;

    public ApiSecurity(IOptions<RepositoryOptions> configured)
    {
        _ = configured.Value.ContentLimits.Validate();
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

        if (string.IsNullOrWhiteSpace(options.Audience) || options.Audience.Length > 200)
            throw new InvalidOperationException("Hosted mode requires a bounded API audience.");
        if (options.Clients.Count == 0)
            throw new InvalidOperationException("Hosted mode requires at least one configured API client.");
        if (string.IsNullOrWhiteSpace(options.RevocationFile))
            throw new InvalidOperationException("Hosted mode requires a host-owned revocation file.");
        revocationFile = Path.GetFullPath(options.RevocationFile);
        _ = ReadRevocations(); // Fail closed at startup as well as on every request.

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ConfiguredClient>();
        foreach (var client in options.Clients)
        {
            var id = client.Id.Trim();
            var keyId = client.KeyId.Trim();
            if (id.Length is < 1 or > 128 || !ids.Add(id))
                throw new InvalidOperationException("Hosted API client ids must be non-empty and unique.");
            if (keyId.Length is < 1 or > 128 || !keyIds.Add(keyId))
                throw new InvalidOperationException("Hosted API key ids must be non-empty and unique.");
            if (client.CredentialSha256.Length != 64 ||
                !client.CredentialSha256.All(character => Uri.IsHexDigit(character)) ||
                !hashes.Add(client.CredentialSha256))
                throw new InvalidOperationException("Each hosted API client requires a unique SHA-256 credential hash.");
            if (string.IsNullOrWhiteSpace(client.Audience) || client.Audience.Length > 200)
                throw new InvalidOperationException("Each hosted API client requires a bounded audience.");
            if (client.ExpiresAt is null)
                throw new InvalidOperationException("Each hosted API client requires an expiry timestamp.");
            var repositories = client.Repositories
                .Where(repository => !string.IsNullOrWhiteSpace(repository))
                .Select(repository => repository.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (repositories.Count == 0)
                throw new InvalidOperationException("Each hosted API client must be registered for at least one repository.");
            var roles = client.Roles
                .Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
            if (roles.Count == 0 || roles.Any(role => !ApiRoles.All.Contains(role)))
                throw new InvalidOperationException("Each hosted API client requires only recognized, explicit roles.");
            if (roles.Contains(ApiRoles.RepositoryAdmin) && !repositories.Contains("*"))
                throw new InvalidOperationException("Repository administrators must explicitly have wildcard repository access.");
            validated.Add(new ConfiguredClient(
                new ApiClientIdentity(id, keyId, client.Audience.Trim(), client.ExpiresAt.Value, repositories, roles),
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

    public ApiAuthenticationDecision AuthenticateDecision(HttpContext context)
    {
        if (IsLocal)
        {
            return new ApiAuthenticationDecision(
                new ApiClientIdentity(
                    "local-development",
                    "local-development",
                    options.Audience,
                    DateTimeOffset.MaxValue,
                    new HashSet<string>(["*"], StringComparer.Ordinal),
                    ApiRoles.All),
                "local-development",
                "authenticated");
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new ApiAuthenticationDecision(null, null, "missing-credential");
        var credential = authorization[prefix.Length..].Trim();
        if (credential.Length == 0)
            return new ApiAuthenticationDecision(null, null, "missing-credential");
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        ConfiguredClient? matched = null;
        foreach (var client in clients)
        {
            if (CryptographicOperations.FixedTimeEquals(suppliedHash, client.CredentialHash))
                matched = client;
        }
        if (matched is null) return new ApiAuthenticationDecision(null, null, "unknown-credential");

        var identity = matched.Identity;
        if (!string.Equals(identity.Audience, options.Audience, StringComparison.Ordinal))
            return new ApiAuthenticationDecision(null, identity.KeyId, "wrong-audience");
        if (identity.ExpiresAt <= DateTimeOffset.UtcNow)
            return new ApiAuthenticationDecision(null, identity.KeyId, "expired");
        try
        {
            if (ReadRevocations().Contains(identity.KeyId))
                return new ApiAuthenticationDecision(null, identity.KeyId, "revoked");
        }
        catch (InvalidOperationException)
        {
            return new ApiAuthenticationDecision(null, identity.KeyId, "revocation-source-unavailable");
        }
        return new ApiAuthenticationDecision(identity, identity.KeyId, "authenticated");
    }

    public ApiClientIdentity? Authenticate(HttpContext context) => AuthenticateDecision(context).Identity;

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

    private IReadOnlySet<string> ReadRevocations()
    {
        if (revocationFile is null) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(revocationFile);
            if (!info.Exists || info.Length > MaxRevocationFileBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("The hosted credential revocation source is missing or unsafe.");
            using var stream = new FileStream(revocationFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, FileOptions.SequentialScan);
            using var bounded = new MemoryStream((int)Math.Min(stream.Length, MaxRevocationFileBytes));
            var buffer = new byte[4096];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                if (bounded.Length + read > MaxRevocationFileBytes)
                    throw new InvalidOperationException("The hosted credential revocation source is oversized.");
                bounded.Write(buffer, 0, read);
            }
            bounded.Position = 0;
            var document = JsonSerializer.Deserialize<RevocationDocument>(bounded, RevocationJsonOptions)
                ?? throw new InvalidOperationException("The hosted credential revocation source is empty.");
            if (document.SchemaVersion != 1 || document.RevokedKeyIds is null ||
                document.RevokedKeyIds.Count > 10_000 ||
                document.RevokedKeyIds.Any(keyId => string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128))
                throw new InvalidOperationException("The hosted credential revocation source is invalid.");
            return document.RevokedKeyIds.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException("The hosted credential revocation source could not be read.", exception);
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

    private sealed record ConfiguredClient(ApiClientIdentity Identity, byte[] CredentialHash);
    private sealed record RevocationDocument(int SchemaVersion, IReadOnlyList<string>? RevokedKeyIds);
}
