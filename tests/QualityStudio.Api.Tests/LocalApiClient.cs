using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QualityStudio.Api.Tests;

internal static class LocalApiClient
{
    public static async Task<HttpClient> CreateLocalClientAsync(
        this WebApplicationFactory<Program> application,
        CancellationToken cancellationToken = default)
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4200");
        var session = await client.GetFromJsonAsync<LocalSecuritySession>(
            "/api/security/csrf", cancellationToken);
        if (session is not { Required: true, Token.Length: > 0 })
        {
            client.Dispose();
            throw new InvalidOperationException("The local API did not issue an anti-CSRF session.");
        }
        client.DefaultRequestHeaders.Add(ApiSecurity.AntiforgeryHeader, session.Token);
        return client;
    }

    private sealed record LocalSecuritySession(bool Required, string Token);
}
