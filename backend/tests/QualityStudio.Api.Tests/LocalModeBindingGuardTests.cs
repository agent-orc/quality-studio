using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class LocalModeBindingGuardTests
{
    [Theory]
    [InlineData("urls", "http://0.0.0.0:5127")]
    [InlineData("urls", "http://127.0.0.1:5127;http://192.168.1.10:5128")]
    [InlineData("urls", "http://*:5127")]
    [InlineData("HTTP_PORTS", "8080")]
    [InlineData("HTTPS_PORTS", "8443")]
    public void Local_mode_refuses_a_configured_address_beyond_loopback(string key, string value)
    {
        var configuration = Configuration((key, value));

        var refused = Assert.Throws<InvalidOperationException>(() =>
            LocalModeBindingGuard.ValidateConfiguredAddresses(configuration, Security(ApiSecurityOptions.LocalMode)));

        Assert.Contains("Local mode", refused.Message, StringComparison.Ordinal);
        Assert.Contains("AllowNonLoopbackLocalMode", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5127")]
    [InlineData("http://localhost:5127;https://localhost:5128")]
    [InlineData("http://[::1]:5127")]
    public void Local_mode_accepts_loopback_addresses(string urls)
    {
        LocalModeBindingGuard.ValidateConfiguredAddresses(
            Configuration(("urls", urls)), Security(ApiSecurityOptions.LocalMode));
    }

    [Fact]
    public void Hosted_mode_and_the_deliberate_override_bind_anywhere()
    {
        var exposed = Configuration(("urls", "http://0.0.0.0:8080"));

        LocalModeBindingGuard.ValidateConfiguredAddresses(exposed, Security(ApiSecurityOptions.HostedMode));
        LocalModeBindingGuard.ValidateConfiguredAddresses(
            exposed, Security(ApiSecurityOptions.LocalMode, allowNonLoopback: true));
    }

    [Fact]
    public async Task A_local_host_that_bound_beyond_loopback_refuses_to_start()
    {
        var guard = Guard(ApiSecurityOptions.LocalMode, "http://[::]:8080");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("http://[::]:8080", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_host_on_loopback_and_an_in_memory_host_start()
    {
        await Guard(ApiSecurityOptions.LocalMode, "http://127.0.0.1:5127")
            .StartAsync(TestContext.Current.CancellationToken);
        await Guard(ApiSecurityOptions.LocalMode).StartAsync(TestContext.Current.CancellationToken);
    }

    private static LocalModeBindingGuard Guard(string mode, params string[] addresses) =>
        new(Options.Create(new RepositoryOptions { Security = Security(mode) }),
            new StubServer(addresses),
            NullLogger<LocalModeBindingGuard>.Instance);

    private static ApiSecurityOptions Security(string mode, bool allowNonLoopback = false) =>
        new() { Mode = mode, AllowNonLoopbackLocalMode = allowNonLoopback };

    private static IConfiguration Configuration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();

    private sealed class StubServer : IServer
    {
        public StubServer(IEnumerable<string> addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses) feature.Addresses.Add(address);
            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
