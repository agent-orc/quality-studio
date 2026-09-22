using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class TrustedProxyConfigurationTests
{
    [Fact]
    public void Forwarding_is_disabled_without_an_explicit_allowlist()
    {
        var options = TrustedProxyConfiguration.Create(new ApiSecurityOptions { Mode = "Hosted" });
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void Only_exact_proxies_and_one_symmetric_hop_are_trusted()
    {
        var options = TrustedProxyConfiguration.Create(new ApiSecurityOptions
        {
            Mode = "Hosted",
            TrustedProxies = ["10.20.30.40", "10.20.30.40", "::1"],
        });
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Contains(IPAddress.Parse("10.20.30.40"), options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("proxy.internal")]
    [InlineData("10.0.0.0/8")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("")]
    public void Invalid_or_wildcard_proxy_addresses_fail_configuration(string proxy)
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(new ApiSecurityOptions
        {
            Mode = "Hosted",
            TrustedProxies = [proxy],
        }));
    }

    [Fact]
    public void Local_mode_cannot_delegate_its_trust_boundary_to_a_proxy()
    {
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Create(new ApiSecurityOptions
        {
            TrustedProxies = ["127.0.0.1"],
        }));
    }
}
