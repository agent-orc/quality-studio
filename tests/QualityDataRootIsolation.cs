using System.Runtime.CompilerServices;
using AgentOrchestrator.CodeQuality;

namespace QualityStudio.Testing;

/// <summary>
/// Points every test in this assembly at a throwaway data root.
/// <para>
/// Since QS-102 a store resolves where it writes from <see cref="QualityWorkspace"/>, which defaults
/// to the user's local application data. Without this the suite would write a project data root per
/// temporary fixture into the developer's real profile and leave it there. Redirecting the base
/// directory once, before any test runs, keeps the suite self-contained; fixtures still get distinct
/// project ids from their distinct temporary checkouts, so tests stay independent of each other.
/// </para>
/// </summary>
internal static class QualityDataRootIsolation
{
    private static string? directory;

    [ModuleInitializer]
    internal static void Redirect()
    {
        directory = Path.Combine(Path.GetTempPath(), "quality-studio-data-root", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(QualityWorkspace.DataRootVariable, directory);
        // The suite has no shared teardown, so the tree is removed when the test host exits. A run
        // killed mid-flight leaves one directory under the temp path, which the OS reclaims.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TemporaryDirectory.Delete(directory);
    }
}
