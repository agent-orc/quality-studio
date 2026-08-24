using System.Diagnostics;

namespace QualityStudio.Testing;

internal static class GitTestRepository
{
    private const string FixtureTimestamp = "2000-01-01T00:00:00Z";

    public static async Task InitializeAsync(string root, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        await RunAsync(root, cancellationToken, "init", "--quiet");
        await RunAsync(root, cancellationToken, "config", "user.email", "quality-studio-tests@example.invalid");
        await RunAsync(root, cancellationToken, "config", "user.name", "Quality Studio Tests");
        await RunAsync(root, cancellationToken, "config", "core.autocrlf", "false");
        await RunAsync(root, cancellationToken, "config", "commit.gpgsign", "false");
    }

    public static void Initialize(string root) => InitializeAsync(root).GetAwaiter().GetResult();

    public static async Task RunAsync(string root, CancellationToken cancellationToken = default,
        params string[] arguments)
    {
        _ = await RunForOutputAsync(root, cancellationToken, arguments);
    }

    public static async Task<string> RunForOutputAsync(string root, CancellationToken cancellationToken = default,
        params string[] arguments) => await RunForOutputAtAsync(root, FixtureTimestamp, cancellationToken, arguments);

    public static async Task<string> RunForOutputAtAsync(string root, string timestamp,
        CancellationToken cancellationToken = default, params string[] arguments)
    {
        using var process = CreateProcess(root, timestamp, arguments);
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git fixture command failed ({process.ExitCode}): git {string.Join(' ', arguments)}\n{output}\n{error}".Trim());
        }
        return output;
    }

    public static void Run(string root, params string[] arguments) =>
        RunAsync(root, default, arguments).GetAwaiter().GetResult();

    public static void RunAt(string root, string timestamp, params string[] arguments) =>
        RunForOutputAtAsync(root, timestamp, default, arguments).GetAwaiter().GetResult();

    public static void Delete(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static Process CreateProcess(string root, string timestamp, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["GIT_AUTHOR_DATE"] = timestamp;
        process.StartInfo.Environment["GIT_COMMITTER_DATE"] = timestamp;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        return process;
    }
}
