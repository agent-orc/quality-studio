using System.Text;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// QS-108 (defect D1): folder aggregates and files above about 20 KB failed to launch on
/// Windows because the review prompt travelled on the command line, which Windows caps at
/// 32,767 characters. The review agent must hand every prompt to the CLI over stdin.
/// </summary>
public sealed class CodingAgentReviewAgentPromptTransportTests
{
    [Fact]
    public void Default_runner_options_send_claude_prompts_over_stdin() =>
        Assert.Equal(ClaudePromptTransport.Stdin, CodingAgentReviewAgent.CreateCliOptions().ClaudePromptTransport);
}

/// <summary>
/// Launches the fake CLI from TestSupport/FakeCodingAgentCli as a real child process through
/// CodingAgentRunner, on both CI legs (ubuntu and windows), and checks where the prompt landed.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class CodingAgentReviewAgentLargePromptTests
{
    private const int PromptCharacters = 100 * 1024;

    /// <summary>cmd.exe's limit, the smallest command-line budget on any supported OS.</summary>
    private const int MaximumArgvCharacters = 8_191;

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task A_100_KB_prompt_reaches_the_cli_intact_over_stdin(string cliType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("quality-studio-prompt-transport-").FullName;
        try
        {
            var fakeCli = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "fake-coding-agent.exe" : "fake-coding-agent");
            Assert.True(File.Exists(fakeCli), $"The fake CLI was not built next to the tests: {fakeCli}");
            var options = CodingAgentReviewAgent.CreateCliOptions() with { ClaudePath = fakeCli, CodexPath = fakeCli };
            var agent = new CodingAgentReviewAgent(cliType, options: options);
            var prompt = BuildPrompt();

            var result = await agent.RunAsync(prompt, root, cancellationToken);

            Assert.Equal("fake review complete", result.Response);
            using var invocation = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(root, "fake-coding-agent-invocation.json"), cancellationToken));
            Assert.Equal(cliType, invocation.RootElement.GetProperty("cli").GetString());
            var stdin = invocation.RootElement.GetProperty("stdin").GetString()!;
            Assert.StartsWith(prompt, stdin.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            var argv = invocation.RootElement.GetProperty("argv").EnumerateArray()
                .Select(argument => argument.GetString()!).ToArray();
            Assert.DoesNotContain(argv, argument => argument.Contains("END OF 100 KB PROMPT", StringComparison.Ordinal));
            Assert.True(argv.Sum(argument => argument.Length + 1) < MaximumArgvCharacters,
                $"argv carried {argv.Sum(argument => argument.Length + 1)} characters.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A multi-line prompt of exactly 100 KiB whose last line is a unique marker.</summary>
    private static string BuildPrompt()
    {
        const string Marker = "END OF 100 KB PROMPT";
        var builder = new StringBuilder(PromptCharacters);
        for (var line = 0; builder.Length < PromptCharacters - Marker.Length - 64; line++)
            builder.Append("line ").Append(line.ToString("D6")).Append(": public sealed class Sample { }\n");
        builder.Append(new string('x', PromptCharacters - Marker.Length - 1 - builder.Length)).Append('\n');
        builder.Append(Marker);
        Assert.Equal(PromptCharacters, builder.Length);
        return builder.ToString();
    }
}
