using System.Text.Json;

namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Finds the TypeScript projects a tsc profile checks in one working directory.
/// <para>
/// An Angular or Vite workspace ships a <em>solution-style</em> <c>tsconfig.json</c>: <c>"files": []</c> plus
/// <c>references</c> to <c>tsconfig.app.json</c> and <c>tsconfig.spec.json</c>. <c>tsc -p</c> on that file
/// compiles no source and exits 0, a false clean. Such a file is therefore replaced by the projects it
/// references, each checked with its own <c>tsc -p</c>. Configs with their own source files are checked
/// alongside their references, so no referenced project is silently omitted. <c>tsc -b</c> would emit build output and
/// <c>.tsbuildinfo</c> files into the analysed checkout.
/// </para>
/// </summary>
public static class TypeScriptProjects
{
    public const string ConfigFileName = "tsconfig.json";
    private const int MaximumProjects = 32;
    private const int MaximumReferenceDepth = 8;
    private const int MaximumConfigBytes = 1_000_000;

    private static readonly JsonDocumentOptions ConfigOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>
    /// The absolute project files to check for <paramref name="workingDirectory"/>, in reference order.
    /// Raises <see cref="ArgumentException"/> when there is nothing checkable or a config cannot be read,
    /// so the sensor reports the reason instead of a clean result or an exception.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string repositoryRoot, string workingDirectory)
    {
        var entry = Path.Combine(workingDirectory, ConfigFileName);
        if (!File.Exists(entry))
        {
            throw new ArgumentException(
                $"No {ConfigFileName} was found in '{Relative(repositoryRoot, workingDirectory)}'. " +
                "Select the tsc profile whose working directory holds the TypeScript project.");
        }
        var projects = new List<string>();
        Collect(repositoryRoot, entry, projects, new HashSet<string>(PathComparer), depth: 0);
        if (projects.Count == 0)
        {
            throw new ArgumentException(
                $"'{Relative(repositoryRoot, entry)}' is solution-style but references no checkable project.");
        }
        return projects;
    }

    /// <summary>
    /// Whether a tsconfig only aggregates references: an explicit empty <c>files</c> list, no
    /// <c>include</c>, and at least one reference. Anything else compiles sources of its own.
    /// </summary>
    public static bool IsSolutionStyle(JsonElement config) =>
        config.ValueKind == JsonValueKind.Object &&
        config.TryGetProperty("files", out var files) &&
        files.ValueKind == JsonValueKind.Array && files.GetArrayLength() == 0 &&
        !config.TryGetProperty("include", out _) &&
        config.TryGetProperty("references", out var references) &&
        references.ValueKind == JsonValueKind.Array && references.GetArrayLength() > 0;

    private static void Collect(
        string repositoryRoot, string configPath, List<string> projects, HashSet<string> visited, int depth)
    {
        if (depth > MaximumReferenceDepth)
            throw new ArgumentException(
                $"'{Relative(repositoryRoot, configPath)}' exceeds the TypeScript project reference depth limit of {MaximumReferenceDepth}.");
        if (!visited.Add(Path.GetFullPath(configPath))) return;
        string text;
        try
        {
            if (new FileInfo(configPath).Length > MaximumConfigBytes)
                throw new ArgumentException($"'{Relative(repositoryRoot, configPath)}' is too large to be a tsconfig.");
            text = File.ReadAllText(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Every caller (scan and availability probe) reports ArgumentException as unavailable.
            throw new ArgumentException(
                $"'{Relative(repositoryRoot, configPath)}' could not be read: {exception.Message}", exception);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, ConfigOptions);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                $"'{Relative(repositoryRoot, configPath)}' is not valid tsconfig JSON: {exception.Message}");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(
                    $"'{Relative(repositoryRoot, configPath)}' must contain a TypeScript project configuration object.");
            if (!IsSolutionStyle(document.RootElement))
            {
                if (projects.Count >= MaximumProjects)
                    throw new ArgumentException(
                        $"TypeScript project reference count exceeds the limit of {MaximumProjects}.");
                projects.Add(configPath);
            }
            if (!document.RootElement.TryGetProperty("references", out var references)) return;
            if (references.ValueKind != JsonValueKind.Array)
                throw new ArgumentException(
                    $"'{Relative(repositoryRoot, configPath)}' has a TypeScript project references value that is not an array.");
            var directory = Path.GetDirectoryName(configPath)!;
            foreach (var reference in references.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.Object ||
                    !reference.TryGetProperty("path", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(pathElement.GetString()))
                    throw new ArgumentException(
                        $"'{Relative(repositoryRoot, configPath)}' has a TypeScript project reference without a valid path.");
                var referenced = Path.GetFullPath(Path.Combine(directory, pathElement.GetString()!));
                if (Directory.Exists(referenced)) referenced = Path.Combine(referenced, ConfigFileName);
                if (!AnalyzerCommand.IsWithin(repositoryRoot, referenced))
                    throw new ArgumentException(
                        $"'{Relative(repositoryRoot, configPath)}' references a project outside the repository: '{pathElement.GetString()}'.");
                if (!File.Exists(referenced))
                    throw new ArgumentException(
                        $"'{Relative(repositoryRoot, configPath)}' references a missing TypeScript project: '{Relative(repositoryRoot, referenced)}'.");
                Collect(repositoryRoot, referenced, projects, visited, depth + 1);
            }
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Relative(string repositoryRoot, string path)
    {
        var relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        return relative == "." ? "the repository root" : relative;
    }
}
