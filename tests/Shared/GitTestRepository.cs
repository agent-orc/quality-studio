using System.Diagnostics;
using System.Text;

namespace QualityStudio.TestSupport;

/// <summary>
/// The single owner of temporary Git repositories in both test assemblies.
/// </summary>
/// <remarks>
/// <para>
/// Real <c>git</c> is a declared tool boundary of the portable lane, not an accident: the
/// hierarchy, staleness, churn, and registry code paths read repository state directly.
/// Centralising the fixture is what makes that boundary honest — one place fixes the
/// identity, the commit timestamps, and the line-ending policy, so a run does not depend on
/// the developer's global <c>~/.gitconfig</c>, and one place reports a missing or failing
/// <c>git</c> as a named diagnostic instead of a bare non-zero exit code.
/// </para>
/// <para>
/// Every repository is created under a fresh temporary root and removed on
/// <see cref="Dispose"/> through <see cref="TestDirectory.Delete"/>.
/// </para>
/// </remarks>
internal sealed class GitTestRepository : IDisposable
{
    /// <summary>Fixed author and committer instant, so commit-derived output is stable.</summary>
    public static readonly DateTimeOffset DefaultTimestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private const string AuthorName = "Quality Studio Test";
    private const string AuthorEmail = "tests@quality-studio.invalid";

    /// <summary>
    /// A path that deliberately does not exist. Git treats a missing GIT_CONFIG_GLOBAL or
    /// GIT_CONFIG_SYSTEM file as an empty configuration, which is exactly the isolation the
    /// suite needs. It is not <c>/dev/null</c>, which does not exist on Windows.
    /// </summary>
    private static readonly string DetachedConfigPath =
        Path.Combine(Path.GetTempPath(), "quality-studio-tests-no-such-gitconfig");

    private GitTestRepository(string root)
    {
        Root = root;
    }

    /// <summary>The repository working directory.</summary>
    public string Root { get; }

    /// <summary>
    /// The parent directory of <see cref="Root"/>. The API refuses repositories outside its
    /// configured allowed roots, so tests need this rather than re-deriving it each time.
    /// </summary>
    public string AllowedRoot => Directory.GetParent(Root)!.FullName;

    /// <summary>Creates an initialised repository under a fresh temporary directory.</summary>
    public static GitTestRepository Create(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = new GitTestRepository(root);
        repository.Initialise();
        return repository;
    }

    /// <summary>Initialises an existing directory as a repository.</summary>
    public static GitTestRepository CreateIn(string root)
    {
        Directory.CreateDirectory(root);
        var repository = new GitTestRepository(root);
        repository.Initialise();
        return repository;
    }

    /// <summary>Writes a file relative to <see cref="Root"/>, creating intermediate directories.</summary>
    public GitTestRepository Write(string relativePath, string content)
    {
        var absolute = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return this;
    }

    /// <summary>Stages everything and commits it at a fixed instant.</summary>
    public GitTestRepository CommitAll(string message, DateTimeOffset? timestamp = null)
    {
        Git("add", "-A");
        return Commit(message, timestamp);
    }

    /// <summary>Commits the current index at a fixed instant.</summary>
    public GitTestRepository Commit(string message, DateTimeOffset? timestamp = null)
    {
        var stamp = (timestamp ?? DefaultTimestamp).ToString("yyyy-MM-ddTHH:mm:ssK");
        Git(
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = stamp,
                ["GIT_COMMITTER_DATE"] = stamp,
            },
            "commit", "--quiet", "--allow-empty", "-m", message);
        return this;
    }

    /// <summary>Runs a git command in this repository and returns its standard output.</summary>
    public string Git(params string[] arguments) => Git(environment: null, arguments);

    /// <summary>Runs a git command with extra environment variables and returns its standard output.</summary>
    public string Git(IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        Run(Root, environment, arguments);

    public void Dispose() => TestDirectory.Delete(Root);

    private void Initialise()
    {
        Run(Root, environment: null, ["init", "--quiet", "--initial-branch", "main"]);
        // Pin the values fixtures actually assert on. The surrounding configuration scopes
        // are detached in Run.
        Git("config", "user.name", AuthorName);
        Git("config", "user.email", AuthorEmail);
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Git("config", "core.eol", "lf");
    }

    private static string Run(string workingDirectory, IReadOnlyDictionary<string, string>? environment, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

        // Repository-local `git config` cannot neutralise everything a developer's global
        // or system configuration can do to a fixture - core.excludesFile alone can make
        // `git add -A` silently skip files a test just wrote. Detaching both scopes is the
        // only way the suite behaves the same on a workstation and on a clean runner.
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = DetachedConfigPath;
        process.StartInfo.Environment["GIT_CONFIG_SYSTEM"] = DetachedConfigPath;
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        if (environment is not null)
        {
            foreach (var (key, value) in environment) process.StartInfo.Environment[key] = value;
        }

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The portable test lane requires 'git' on PATH, but starting it failed: {exception.Message}. " +
                "Provision git in the job rather than skipping the affected tests.",
                exception);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(Describe(workingDirectory, arguments, process.ExitCode, output, error));
        }

        return output;
    }

    private static string Describe(string workingDirectory, string[] arguments, int exitCode, string output, string error)
    {
        var builder = new StringBuilder();
        builder.Append("git ").Append(string.Join(' ', arguments)).Append(" failed with exit code ").Append(exitCode).AppendLine(".");
        builder.Append("working directory: ").AppendLine(workingDirectory);
        builder.AppendLine("--- stdout ---").AppendLine(output.Length == 0 ? "(empty)" : output.TrimEnd());
        builder.AppendLine("--- stderr ---").Append(error.Length == 0 ? "(empty)" : error.TrimEnd());
        return builder.ToString();
    }
}
