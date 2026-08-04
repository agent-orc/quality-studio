namespace AgentOrchestrator.CodeQuality.Tests;

internal static class TestDirectory
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must not turn a successful assertion into a platform-specific
            // failure. Git can retain handles briefly and marks object files read-only
            // on Windows, so abandoned temporary fixtures are preferable to a false red.
        }
    }
}
