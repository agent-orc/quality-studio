using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace QualityStudio.Api.Tests;

/// <summary>
/// The registered roots reach the Git processes the host starts as <c>safe.directory</c>. Whether
/// Git then reads a checkout owned by another uid needs a second user, which only the container
/// check (<c>scripts/check-container-git.mjs</c>) has.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class GitSafeDirectoriesTests
{
    private static readonly string[] Variables =
        ["GIT_CONFIG_COUNT", .. Enumerable.Range(0, 4).SelectMany(index => new[] { $"GIT_CONFIG_KEY_{index}", $"GIT_CONFIG_VALUE_{index}" })];

    [Fact]
    public void Registered_roots_become_safe_directories_next_to_inherited_configuration()
    {
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var first = Path.Combine(Path.GetTempPath(), "qs-safe-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "qs-safe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "qualitystudio.marker");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "inherited");
            var trust = new GitSafeDirectories(Configuration(enabled: true));

            trust.Trust([first, second, first]);

            var safe = Git("config", "--get-all", "safe.directory");
            Assert.Contains(first, safe);
            Assert.Contains(second, safe);
            Assert.Equal(["inherited"], Git("config", "--get", "qualitystudio.marker"));
            Assert.Equal("3", Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));

            trust.Trust([second]);

            safe = Git("config", "--get-all", "safe.directory");
            Assert.DoesNotContain(first, safe);
            Assert.Contains(second, safe);
            Assert.Null(Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_2"));
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void Without_the_setting_the_environment_is_left_alone()
    {
        var before = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        var trust = new GitSafeDirectories(Configuration(enabled: false));

        trust.Trust([Path.GetTempPath()]);

        Assert.False(trust.Enabled);
        Assert.Equal(before, Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"));
    }

    private static IConfiguration Configuration(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [GitSafeDirectories.ConfigurationKey] = enabled.ToString() })
        .Build();

    private static string[] Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
