using System.Collections.Concurrent;
using System.Reflection;
using Json.Schema;

namespace AgentOrchestrator.CodeQuality.Tests;

internal static class RepositoryTestContext
{
    public const string RootEnvironmentVariable = "QUALITY_STUDIO_REPOSITORY_ROOT";
    private const string RootMetadataName = "QualityStudioRepositoryRoot";
    private const string RootMarker = "QualityStudio.slnx";
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new(StringComparer.Ordinal);

    public static JsonSchema Schema(string fileName) => Schemas.GetOrAdd(fileName, name => new Lazy<JsonSchema>(
        () => JsonSchema.FromText(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "schemas", name))),
        LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public static string FindRepositoryRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return RequireRepositoryRoot(overrideRoot, $"environment variable {RootEnvironmentVariable}");

        var embeddedRoot = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == RootMetadataName)?.Value;
        if (!string.IsNullOrWhiteSpace(embeddedRoot) && IsRepositoryRoot(embeddedRoot))
            return Path.GetFullPath(embeddedRoot);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (IsRepositoryRoot(directory.FullName)) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Quality Studio repository root was not found. Set {RootEnvironmentVariable} to a directory containing {RootMarker}.");
    }

    private static string RequireRepositoryRoot(string path, string source)
    {
        var fullPath = Path.GetFullPath(path);
        if (IsRepositoryRoot(fullPath)) return fullPath;
        throw new DirectoryNotFoundException(
            $"The repository root from {source} does not contain {RootMarker}: {fullPath}");
    }

    private static bool IsRepositoryRoot(string path) => File.Exists(Path.Combine(path, RootMarker));
}
