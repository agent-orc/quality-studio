using System.Diagnostics;

namespace QualityStudio.Testing;

public sealed class GitTestRepository : IDisposable
{
    private readonly bool ownsDirectory;

    private GitTestRepository(string root, bool ownsRoot)
    {
        Root = root;
        ownsDirectory = ownsRoot;
    }

    public string Root { get; }

    public static GitTestRepository Create(string prefix)
    {
        var repository = new GitTestRepository(
            Directory.CreateTempSubdirectory(prefix).FullName,
            ownsRoot: true);
        repository.Initialize();
        return repository;
    }

    public static GitTestRepository InitializeAt(string root)
    {
        Directory.CreateDirectory(root);
        var repository = new GitTestRepository(Path.GetFullPath(root), ownsRoot: false);
        repository.Initialize();
        return repository;
    }

    public static GitTestRepository At(string root) =>
        new(Path.GetFullPath(root), ownsRoot: false);

    public void Initialize()
    {
        Run("init", "--quiet");
        Run("config", "user.email", "fixture@quality-studio.test");
        Run("config", "user.name", "Quality Studio Fixture");
        Run("config", "core.autocrlf", "false");
    }

    public string Run(params string[] arguments) => Run(arguments, null);

    public string Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        using var process = CreateProcess(arguments, environment);
        if (!process.Start()) throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        return output;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = CreateProcess(arguments, null);
        if (!process.Start()) throw new InvalidOperationException("Git did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error}");
        return output;
    }

    public void Dispose()
    {
        if (!ownsDirectory || !Directory.Exists(Root)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A retained fixture is preferable to turning a successful test red during cleanup.
        }
    }

    private Process CreateProcess(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var (key, value) in environment) process.StartInfo.Environment[key] = value;
        return process;
    }
}
