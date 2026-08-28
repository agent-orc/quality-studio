using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace QualityStudio.Api;

// Local mode has no bearer credential (ApiSecurity.cs) and relies entirely on the operating system
// only exposing the API to the local machine (docs/operations/security/index.html, F-06). The standard
// launcher binds loopback correctly, but nothing previously stopped the API itself from serving Local
// mode traffic if it were ever started with a non-loopback --urls/ASPNETCORE_URLS value.
public static class LocalModeBindingGuard
{
    public static bool IsLoopbackAddress(string boundAddress)
    {
        if (!Uri.TryCreate(boundAddress, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    public static IReadOnlyList<string> FindNonLoopbackAddresses(IEnumerable<string> boundAddresses) =>
        boundAddresses.Where(address => !IsLoopbackAddress(address)).ToArray();

    // The addresses Kestrel actually bound are only known once the server has started (they depend on
    // --urls / ASPNETCORE_URLS / config, none of which are reliably readable before then), so this can
    // only fail fast immediately after startup rather than before it. setExitCode is injectable so tests
    // can observe the decision without mutating the shared test-process Environment.ExitCode.
    public static void EnforceLoopbackBinding(WebApplication app, bool isLocal, Action<int>? setExitCode = null)
    {
        if (!isLocal)
        {
            return;
        }

        setExitCode ??= code => Environment.ExitCode = code;
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                ?? [];
            var nonLoopback = FindNonLoopbackAddresses(addresses);
            if (nonLoopback.Count == 0)
            {
                return;
            }

            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("QualityStudio.Api.Startup");
            logger.LogCritical(
                "Local mode API bound to non-loopback address(es) {Addresses}; QualityStudio:Security:Mode is Local " +
                "so the process is not safe to keep serving and is shutting down.",
                string.Join(", ", nonLoopback));
            setExitCode(1);
            app.Lifetime.StopApplication();
        });
    }
}
