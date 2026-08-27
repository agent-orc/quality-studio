using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace QualityStudio.Api.Tests;

internal static class LocalApiClient
{
    public static HttpClient Create(WebApplicationFactory<Program> application)
    {
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        using var response = client.GetAsync("/api/security/session").GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var session = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        var headerName = session.GetProperty("headerName").GetString()!;
        var token = session.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://localhost:4200");
        client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, token);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie",
            $"{ApiSecurity.AntiCsrfCookie}={token}");
        return client;
    }
}
