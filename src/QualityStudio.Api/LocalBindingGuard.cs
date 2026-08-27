namespace QualityStudio.Api;

/// <summary>
/// Local mode grants wildcard registrar access with no credential (see <see cref="ApiSecurity.Authenticate"/>).
/// That trust model only holds if the API is actually confined to loopback, so startup fails fast rather
/// than silently exposing an unauthenticated host on the network.
/// </summary>
public static class LocalBindingGuard
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public static void EnsureLoopbackWhenLocal(bool isLocal, string? configuredUrls)
    {
        if (!isLocal || string.IsNullOrWhiteSpace(configuredUrls)) return;

        foreach (var candidate in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsLoopbackUrl(candidate))
            {
                throw new InvalidOperationException(
                    $"QualityStudio:Security:Mode is Local, which grants unauthenticated wildcard repository " +
                    $"access, but the configured URL '{candidate}' is not bound to loopback. Bind to " +
                    "localhost/127.0.0.1/::1, or switch to Hosted mode with configured API clients.");
            }
        }
    }

    private static bool IsLoopbackUrl(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        return LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
