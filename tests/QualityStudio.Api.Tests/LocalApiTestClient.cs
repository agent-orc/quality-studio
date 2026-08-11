using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QualityStudio.Api.Tests;

internal static class LocalApiTestClient
{
    public const string AllowedOrigin = "http://localhost:4200";

    public static HttpClient Create(
        WebApplicationFactory<Program> application,
        WebApplicationFactoryClientOptions? options = null)
    {
        var client = application.CreateClient(options ?? new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Add("Origin", AllowedOrigin);
        var response = client.GetAsync("/api/security/csrf").GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var token = response.Content.ReadFromJsonAsync<LocalMutationTokenResponse>().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The local anti-CSRF endpoint returned no token.");
        client.DefaultRequestHeaders.Add(LocalMutationProtection.HeaderName, token.Token);
        return client;
    }
}
