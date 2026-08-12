using System.Diagnostics;

namespace QualityStudio.Testing;

internal static class GitFixture
{
    private const string FixedDate = "2026-01-01T00:00:00Z";

    public static string Create(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Initialize(root);
        return root;
    }

    public static void Initialize(string root)
    {
        Directory.CreateDirectory(root);
        Run(root, "init", "--quiet");
        Run(root, "config", "user.email", "quality-studio-tests@example.invalid");
        Run(root, "config", "user.name", "Quality Studio Tests");
        Run(root, "config", "core.autocrlf", "false");
    }

    public static async Task InitializeAsync(string root, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        await RunAsync(root, cancellationToken, "init", "--quiet");
        await RunAsync(root, cancellationToken, "config", "user.email", "quality-studio-tests@example.invalid");
        await RunAsync(root, cancellationToken, "config", "user.name", "Quality Studio Tests");
        await RunAsync(root, cancellationToken, "config", "core.autocrlf", "false");
    }

    public static string Run(string root, params string[] arguments) =>
        RunAsync(root, CancellationToken.None, arguments).GetAwaiter().GetResult();

    public static async Task<string> RunAsync(
        string root,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.Environment["GIT_AUTHOR_DATE"] = FixedDate;
        process.StartInfo.Environment["GIT_COMMITTER_DATE"] = FixedDate;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start()) throw new InvalidOperationException("Git did not start.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error.Trim()}");
        }
        return output.Trim();
    }
}
