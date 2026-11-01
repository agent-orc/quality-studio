using System.Reflection;

namespace QualityStudio.Api;

/// <summary>
/// What this host offers Agent Studio, served at <c>GET /api/v1/capabilities</c>. Agent Studio reads it
/// before using an integration surface and degrades the surface when a feature is missing, so a
/// feature is listed here only once its routes exist.
/// </summary>
public static class IntegrationCapabilities
{
    public const string Contract = "agent-studio-integration/1";

    /// <summary>Registrations carry <c>externalRefs</c>, and <c>GET /api/v1/repos?externalRef=</c> finds them.</summary>
    public const string ExternalRefFeature = "external-ref";

    public static IReadOnlyList<string> Features { get; } = [ExternalRefFeature];

    public static object Describe() => new
    {
        product = "quality-studio",
        version = typeof(IntegrationCapabilities).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
        contracts = new[] { Contract },
        features = Features,
    };
}
