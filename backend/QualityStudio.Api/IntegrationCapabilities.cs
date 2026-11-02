using System.Reflection;

namespace QualityStudio.Api;

/// <summary>
/// What this host offers an integrating Agent Studio (contract <c>agent-studio-integration/1</c>).
/// A caller reads the feature list and degrades a surface whose feature is absent instead of
/// failing. Any authenticated client may read it; it names no repository.
/// </summary>
internal static class IntegrationCapabilities
{
    public const string Route = "/api/v1/capabilities";
    public const string Contract = "agent-studio-integration/1";

    /// <summary>The verdict store: <c>PUT</c>/<c>GET /api/v1/repos/{id}/change-reviews</c> and cache-first <c>change-review</c>.</summary>
    public const string ChangeReviewStore = "change-review-store";

    public static object Document { get; } = new
    {
        product = "quality-studio",
        version = typeof(IntegrationCapabilities).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        contracts = new[] { Contract },
        features = new[] { ChangeReviewStore },
    };
}
