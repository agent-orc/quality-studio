using System.Net.Http.Json;
using System.Text.Json;

namespace QualityStudio.Api.Tests;

/// <summary>
/// Test-only convenience handler: fetches a fresh anti-CSRF nonce for every mutation request that
/// doesn't already carry one, so integration/smoke tests that aren't specifically exercising CSRF
/// behavior don't need to fetch and attach a nonce by hand at every call site. Tests that DO exercise
/// CSRF behavior (see ApiSecurityTests) create their client without this handler and manage the
/// nonce header explicitly.
/// </summary>
internal sealed class CsrfNonceHandler : DelegatingHandler
{
    private static readonly HashSet<HttpMethod> MutationMethods =
        [HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (MutationMethods.Contains(request.Method) && !request.Headers.Contains("X-Csrf-Nonce"))
        {
            var nonceUri = new Uri(request.RequestUri!, "/api/security/csrf-nonce");
            using var nonceRequest = new HttpRequestMessage(HttpMethod.Get, nonceUri);
            using var nonceResponse = await base.SendAsync(nonceRequest, cancellationToken);
            if (nonceResponse.IsSuccessStatusCode)
            {
                var body = await nonceResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
                request.Headers.Add("X-Csrf-Nonce", body.GetProperty("nonce").GetString());
            }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
