using System.Diagnostics;

internal static class TestToolProcess
{
    public static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null) =>
        await RunAsync("git", workingDirectory, arguments, cancellationToken, environment);

    public static async Task<string> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (name, value) in environment) process.StartInfo.Environment[name] = value;

        try
        {
            if (!process.Start()) throw new InvalidOperationException("Git did not start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Unable to start declared {executable} fixture in '{workingDirectory}'.", exception);
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.\nstdout:\n{output}\nstderr:\n{error}");
        return output;
    }

    public static string RunGit(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null) =>
        RunGitAsync(workingDirectory, arguments, environment: environment).GetAwaiter().GetResult();

    public static async Task InitializeGitRepositoryAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        await RunGitAsync(workingDirectory, ["init", "--quiet"], cancellationToken);
        await RunGitAsync(workingDirectory, ["config", "user.email", "quality-tests@example.test"], cancellationToken);
        await RunGitAsync(workingDirectory, ["config", "user.name", "Quality Studio Tests"], cancellationToken);
        await RunGitAsync(workingDirectory, ["config", "core.autocrlf", "false"], cancellationToken);
    }
}
