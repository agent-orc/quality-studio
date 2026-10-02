using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace QualityStudio.Api;

/// <summary>Opt-in scheme forwarding from exactly the proxy addresses configured by the host.</summary>
public static class TrustedProxyConfiguration
{
    /// <summary>
    /// The framework can add its own permissive forwarding middleware before this app's
    /// middleware. Reject that switch instead of letting it bypass the explicit proxy list.
    /// </summary>
    public static void ValidateHostingConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(configuration["ForwardedHeaders_Enabled"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED enables unrestricted forwarding before the API. " +
                "Disable it and configure QualityStudio:Security:TrustedProxies instead.");
    }

    public static ForwardedHeadersOptions Create(ApiSecurityOptions security)
    {
        ArgumentNullException.ThrowIfNull(security);
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.None };
        if (security.TrustedProxies.Length == 0) return options;
        if (!string.Equals(security.Mode, ApiSecurityOptions.HostedMode, StringComparison.Ordinal))
            throw new InvalidOperationException("Trusted proxies require Hosted security mode.");

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var value in security.TrustedProxies)
        {
            if (!IPAddress.TryParse(value, out var address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException(
                    "QualityStudio:Security:TrustedProxies must contain explicit proxy IP addresses.");
            if (!options.KnownProxies.Contains(address)) options.KnownProxies.Add(address);
        }

        // Preserve the original Host: only scheme and client address are needed for TLS termination.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.RequireHeaderSymmetry = true;
        return options;
    }
}
