using System.Reflection;
using System.Runtime.CompilerServices;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Testing;

/// <summary>
/// Points the studio's data root at a temporary directory for the whole test assembly.
/// <para>
/// Every store resolves its paths through <see cref="QualityDataRoot"/>, whose default is the
/// developer's own <c>%LOCALAPPDATA%</c>. Without this, a test run would write its fixtures into
/// the real data root of whoever ran it and read back another run's leftovers. A module
/// initializer is the only hook that is guaranteed to run before the first test constructs a
/// store, whichever test that turns out to be.
/// </para>
/// <para>
/// Tests stay isolated from each other the same way they already were: each uses its own temporary
/// repository root, and distinct repository roots resolve to distinct project directories.
/// </para>
/// </summary>
/// <remarks>
/// The redirection is published twice on purpose. An API test hosts the real
/// <c>QualityStudio.Api</c> in this process, and that host configures its own data root while it
/// builds; with no <c>DataRoot</c> in its configuration it configures the default, which would
/// undo this fixture for every test that follows. The environment variable is what the default
/// resolution then reads, so an in-process host lands in the same temporary directory.
/// </remarks>
internal static class QualityDataRootFixture
{
    /// <summary>The temporary base directory every project's data root resolves below.</summary>
    internal static string BaseDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void RedirectToTemporaryDirectory()
    {
        // Keyed by assembly and process so two test assemblies, or two runs in parallel, never
        // share a project directory - and so a crashed run leaves at most one stale folder behind.
        var root = Path.Combine(
            Path.GetTempPath(),
            "quality-studio-tests",
            Assembly.GetExecutingAssembly().GetName().Name ?? "tests",
            Environment.ProcessId.ToString());
        TemporaryDirectory.Delete(root);
        Directory.CreateDirectory(root);
        BaseDirectory = root;
        Environment.SetEnvironmentVariable(QualityDataRoot.EnvironmentVariable, root);
        QualityDataRoot.Configure(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TemporaryDirectory.Delete(root);
    }
}
