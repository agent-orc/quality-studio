namespace QualityStudio.Api;

/// <summary>
/// Shared names for the Agent Studio integration contract. The live capabilities document is
/// assembled in <see cref="FederationEndpoints"/> from configured host features.
/// </summary>
internal static class IntegrationCapabilities
{
    public const string Contract = "agent-studio-integration/1";

    /// <summary>The verdict store: <c>PUT</c>/<c>GET /api/v1/repos/{id}/change-reviews</c> and cache-first <c>change-review</c>.</summary>
    public const string ChangeReviewStore = "change-review-store";

}
