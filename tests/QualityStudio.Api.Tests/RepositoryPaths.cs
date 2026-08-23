namespace QualityStudio.Api.Tests;

internal static class RepositoryPaths
{
    private const string RootMarker = "QualityStudio.slnx";

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RootMarker))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Quality Studio repository root ({RootMarker}) was not found above {AppContext.BaseDirectory}.");
    }
}
