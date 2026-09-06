using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace QualityStudio.Api;

/// <summary>
/// Local mode answers every request as a registrar with wildcard repository access. That is only
/// defensible while nothing but this machine can reach the listener, so a non-loopback binding in
/// Local mode stops the host instead of quietly publishing an unauthenticated API.
/// </summary>
public sealed class LocalModeBindingGuard : IHostedService
{
    /// <summary>Configuration keys Kestrel reads for its listen addresses.</summary>
    private static readonly string[] AddressKeys = ["urls", "HTTP_PORTS", "HTTPS_PORTS"];

    private readonly ApiSecurityOptions options;
    private readonly IServer server;
    private readonly ILogger<LocalModeBindingGuard> logger;

    public LocalModeBindingGuard(IOptions<RepositoryOptions> configured, IServer server,
        ILogger<LocalModeBindingGuard> logger)
    {
        options = configured.Value.Security;
        this.server = server;
        this.logger = logger;
    }

    /// <summary>
    /// Refuses a Local-mode host whose configured addresses already reach beyond loopback. This runs
    /// before the listener opens; <see cref="StartAsync"/> repeats the check against the addresses
    /// the server actually bound, which is the authoritative one.
    /// </summary>
    public static void ValidateConfiguredAddresses(IConfiguration configuration, ApiSecurityOptions security)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(security);
        if (!IsLocalMode(security) || security.AllowNonLoopbackLocalMode) return;
        var exposed = AddressKeys
            .SelectMany(key => SplitAddresses(configuration[key]))
            .Where(address => !IsLoopback(address))
            .ToArray();
        if (exposed.Length > 0) throw new InvalidOperationException(Message(exposed));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsLocalMode(options) || options.AllowNonLoopbackLocalMode) return Task.CompletedTask;
        // An in-memory test server binds nothing; there is no address to judge.
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0) return Task.CompletedTask;
        var exposed = addresses.Where(address => !IsLoopback(address)).ToArray();
        if (exposed.Length == 0) return Task.CompletedTask;
        var message = Message(exposed);
        logger.LogCritical(new EventId(1610, "LocalModeBoundBeyondLoopback"), "{Reason}", message);
        throw new InvalidOperationException(message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsLocalMode(ApiSecurityOptions security) =>
        string.Equals(security.Mode, ApiSecurityOptions.LocalMode, StringComparison.Ordinal);

    private static IEnumerable<string> SplitAddresses(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// A bare port list (<c>HTTP_PORTS</c>) binds every interface, and <c>*</c>, <c>+</c>, <c>0.0.0.0</c>
    /// and <c>[::]</c> do not parse as loopback URIs. Anything this cannot prove to be loopback counts
    /// as exposed.
    /// </summary>
    private static bool IsLoopback(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static string Message(IReadOnlyCollection<string> exposed) =>
        $"Quality Studio runs in Local mode, where every request is treated as a registrar, but it is " +
        $"bound to {string.Join(", ", exposed)}, which is reachable from outside this machine. Bind to " +
        "127.0.0.1 (for example --urls http://127.0.0.1:5127), switch to " +
        "QualityStudio:Security:Mode=Hosted and configure API clients, or accept the exposure " +
        "deliberately with QualityStudio:Security:AllowNonLoopbackLocalMode=true.";
}
