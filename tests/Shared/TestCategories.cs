namespace QualityStudio.TestSupport;

/// <summary>
/// The lane taxonomy the CI gate selects on. Every test that is not portable has to
/// declare which non-portable lane it belongs to, otherwise the portable lane silently
/// inherits a host, timing, or network dependency it does not provision.
/// </summary>
/// <remarks>
/// Portable is the default and carries no trait: deterministic assertions over in-memory
/// state, recorded fixtures, or tools the required job explicitly provisions (git, dotnet).
/// </remarks>
internal static class TestCategories
{
    /// <summary>
    /// Wall-clock or throughput assertions whose result depends on the host. These run as
    /// repeated release-canary samples with recorded host metadata, never in the PR gate.
    /// </summary>
    public const string MachineBound = "MachineBound";

    /// <summary>
    /// Assertions that need a live external service (an agent CLI, a package registry, a
    /// download). A failure here has to be classified as unavailable-dependency before it
    /// is read as a product regression, so these are opt-in canary checks.
    /// </summary>
    public const string ExternalLive = "ExternalLive";
}
