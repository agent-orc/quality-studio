namespace AgentOrchestrator.CodeQuality;

/// <summary>
/// Launches the bundled Windows npm/npx JavaScript CLIs with Node instead of invoking command shims.
/// Only absolute host PATH entries participate; repository working directories are never searched.
/// </summary>
internal static class WindowsNpmCommand
{
    internal sealed record Launch(string Executable, IReadOnlyList<string> Arguments);

    public static Launch Resolve(IReadOnlyList<string> arguments, string command = "npm") =>
        Resolve(arguments, (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator), command);

    internal static Launch Resolve(IReadOnlyList<string> arguments, IEnumerable<string> pathEntries, string command = "npm")
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(pathEntries);
        var cliName = command.ToLowerInvariant() switch
        {
            "npm" => "npm-cli.js",
            "npx" => "npx-cli.js",
            _ => throw new ArgumentException("Only the bundled npm and npx CLIs are supported.", nameof(command)),
        };
        var directories = pathEntries.Select(AbsoluteDirectory).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var directory in directories)
        {
            var cli = Path.Combine(directory, "node_modules", "npm", "bin", cliName);
            if (!File.Exists(cli)) continue;

            // An npm installation bundled with Node must use that matching runtime. A global
            // npm install may have no adjacent Node, in which case use the host PATH runtime.
            string? node = Path.Combine(directory, "node.exe");
            if (!File.Exists(node))
                node = directories.Select(path => Path.Combine(path, "node.exe")).FirstOrDefault(File.Exists);
            if (node is null)
                throw new SecurityScannerUnavailableException(
                    $"{command} could not be launched: its JavaScript CLI was found, but node.exe was not available on the host PATH.");
            return new Launch(node, [cli, .. arguments]);
        }

        throw new SecurityScannerUnavailableException(
            $"{command} could not be launched: no node_modules/npm/bin/{cliName} was found in an absolute host PATH directory.");
    }

    private static string? AbsoluteDirectory(string entry)
    {
        var path = entry.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(path)) return null;
        try
        {
            var directory = Path.GetFullPath(path);
            return Directory.Exists(directory) ? directory : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
