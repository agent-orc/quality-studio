using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class LocalRequestTrustTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5127")]
    [InlineData("[::1]:5127")]
    public void Local_requests_allow_loopback_authorities(string host)
    {
        Assert.True(Security().IsLocalRequestTrusted(Request(host)));
    }

    [Theory]
    [InlineData("attacker.example")]
    [InlineData("localhost.attacker.example")]
    [InlineData("127.0.0.1.attacker.example")]
    [InlineData("user@localhost")]
    [InlineData("localhost/other")]
    [InlineData("")]
    public void Local_requests_reject_untrusted_or_malformed_authorities(string host)
    {
        Assert.False(Security().IsLocalRequestTrusted(Request(host)));
    }

    [Theory]
    [InlineData("http://localhost:4200.attacker.example")]
    [InlineData("http://localhost.attacker.example:4200")]
    [InlineData("http://localhost:4200@attacker.example")]
    [InlineData("http://user@localhost:4200")]
    [InlineData("http://localhost:4200/path")]
    [InlineData("http://localhost:4200?query")]
    [InlineData("http://localhost:4200#fragment")]
    [InlineData("http://localhost:4201")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("file:///")]
    [InlineData("http://localhost:4200, https://attacker.example")]
    public void Local_requests_reject_untrusted_or_malformed_origins(string origin)
    {
        var context = Request("localhost:5127");
        context.Request.Headers.Origin = origin;
        Assert.False(Security().IsLocalRequestTrusted(context));
    }

    [Fact]
    public void Local_requests_reject_multiple_origin_headers()
    {
        var context = Request("localhost:5127");
        context.Request.Headers.Origin = new[] { "http://localhost:4200", "https://attacker.example" };
        Assert.False(Security().IsLocalRequestTrusted(context));
    }

    [Fact]
    public void Configured_frontend_is_allowed_even_when_fetch_metadata_says_cross_site()
    {
        var context = Request("127.0.0.1:5127");
        context.Request.Headers.Origin = "http://localhost:4200";
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.True(Security().IsLocalRequestTrusted(context));
    }

    [Fact]
    public void Same_origin_matches_default_port_but_not_another_scheme()
    {
        var context = Request("localhost:80");
        context.Request.Headers.Origin = "http://localhost";
        Assert.True(Security().IsLocalRequestTrusted(context));
        context.Request.Headers.Origin = "https://localhost";
        Assert.False(Security().IsLocalRequestTrusted(context));
    }

    [Fact]
    public void Explicit_network_local_mode_override_does_not_disable_browser_origin_checks()
    {
        var security = Security(allowNetwork: true);
        var context = Request("studio.internal:5127");
        Assert.True(security.IsLocalRequestTrusted(context));
        context.Request.Headers.Origin = "https://attacker.example";
        Assert.False(security.IsLocalRequestTrusted(context));
        context.Request.Headers.Origin = "http://studio.internal:5127";
        Assert.True(security.IsLocalRequestTrusted(context));
    }

    [Fact]
    public void Hosted_mode_keeps_its_bearer_authentication_boundary()
    {
        var options = new RepositoryOptions
        {
            Security = new ApiSecurityOptions
            {
                Mode = ApiSecurityOptions.HostedMode,
                Clients = [new ApiClientOptions
                {
                    Id = "test",
                    CredentialSha256 = new string('a', 64),
                    Repositories = ["default"],
                }],
            },
        };
        Assert.True(new ApiSecurity(Options.Create(options)).IsLocalRequestTrusted(Request("studio.example")));
    }

    private static ApiSecurity Security(bool allowNetwork = false) =>
        new(Options.Create(new RepositoryOptions
        {
            Security = new ApiSecurityOptions { AllowNonLoopbackLocalMode = allowNetwork },
        }));

    private static DefaultHttpContext Request(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        return context;
    }
}
