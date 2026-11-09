namespace QualityStudio.Api;

/// <summary>
/// Shared identifiers for the Agent Studio integration contract. The capabilities response in
/// <see cref="FederationEndpoints"/> is the single description of what this host offers.
/// </summary>
internal static class IntegrationCapabilities
{
    public const string Contract = "agent-studio-integration/1";

    /// <summary>The verdict store: <c>PUT</c>/<c>GET /api/v1/repos/{id}/change-reviews</c> and cache-first <c>change-review</c>.</summary>
    public const string ChangeReviewStore = "change-review-store";
}
