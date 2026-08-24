using System.Diagnostics;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class GitSourceRevisionReaderTests
{
    [Fact]
    public async Task Reads_the_head_commit_branch_and_working_tree_state()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-revision-").FullName;
        try
        {
            Git(root, "init");
            Git(root, "symbolic-ref", "HEAD", "refs/heads/trunk");
            Git(root, "config", "user.email", "fixture@example.invalid");
            Git(root, "config", "user.name", "Fixture");
            File.WriteAllText(Path.Combine(root, "App.cs"), "// reviewed\n");
            Git(root, "add", "App.cs");
            Git(root, "commit", "-m", "fixture commit");

            var clean = await GitSourceRevisionReader.ReadAsync(root, TestContext.Current.CancellationToken);
            Assert.NotNull(clean);
            Assert.Matches("^[a-f0-9]{40,64}$", clean.CommitSha);
            Assert.Equal(clean.CommitSha[..12], clean.ShortCommitSha);
            Assert.Equal("trunk", clean.Branch);
            Assert.False(clean.Dirty);
            Assert.NotNull(clean.CommittedAt);

            File.WriteAllText(Path.Combine(root, "App.cs"), "// edited after the commit\n");
            var modified = await GitSourceRevisionReader.ReadAsync(root, TestContext.Current.CancellationToken);
            Assert.NotNull(modified);
            Assert.Equal(clean.CommitSha, modified.CommitSha);
            Assert.True(modified.Dirty);

            // An untracked file is reviewed even though it does not exist at the pinned commit, so it
            // must count as a modified tree.
            Git(root, "checkout", "--", "App.cs");
            File.WriteAllText(Path.Combine(root, "Untracked.cs"), "// never added\n");
            var untracked = await GitSourceRevisionReader.ReadAsync(root, TestContext.Current.CancellationToken);
            Assert.NotNull(untracked);
            Assert.True(untracked.Dirty);
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    [Fact]
    public async Task Reports_no_revision_when_the_repository_cannot_be_resolved()
    {
        var root = Directory.CreateTempSubdirectory("quality-run-revision-absent-").FullName;
        try
        {
            // A `.git` file is a repository pointer. An unresolvable one makes Git fail deterministically
            // regardless of whether the temporary directory happens to sit inside another working tree.
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ./does-not-exist\n");
            Assert.Null(await GitSourceRevisionReader.ReadAsync(root, TestContext.Current.CancellationToken));
            Assert.Null(await GitSourceRevisionReader.ReadAsync(
                Path.Combine(root, "missing"), TestContext.Current.CancellationToken));
            Assert.Null(await GitSourceRevisionReader.ReadAsync("  ", TestContext.Current.CancellationToken));
        }
        finally
        {
            TestDirectory.Delete(root);
        }
    }

    /// <summary>
    /// Seeds a fixture repository with the ambient Git configuration neutralised, so a developer or
    /// image that signs commits, installs hooks, or ships a global excludes file cannot fail the test.
    /// </summary>
    internal static void Git(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(root, ".gitconfig-absent");
        process.StartInfo.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(root, ".gitconfig-absent");
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var setting in new[] { "commit.gpgsign=false", "core.hooksPath=", "core.autocrlf=false" })
        {
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(setting);
        }
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
    }
}
