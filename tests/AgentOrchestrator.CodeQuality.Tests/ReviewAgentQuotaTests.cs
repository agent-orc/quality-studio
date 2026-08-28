using System.Diagnostics;
using CodingAgentRunner.Abstractions;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Regression coverage for the dossier's S1 "enforce wall-clock … and output-byte
/// … quotas" exit criterion (docs/operations/security/index.html §08 S1): before
/// this change, <see cref="CodingAgentReviewAgent.RunAsync"/> had no wall-clock
/// timeout and its output <see cref="System.Text.StringBuilder"/> grew unbounded.
/// </summary>
public sealed class ReviewAgentQuotaTests
{
    [Fact]
    public async Task RunAsync_CompletesNormally_WhenWithinDefaultQuotas()
    {
        var spawner = new ScriptedSpawner(
        [
            "{\"type\":\"thread.started\",\"thread_id\":\"t1\"}",
            "{\"type\":\"turn.started\"}",
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"hello world\"}}",
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}",
        ]);
        var agent = new CodingAgentReviewAgent("codex", options: new CliOptions { Spawner = spawner });

        var result = await agent.RunAsync(
            "review this repository", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        Assert.Equal("hello world", result.Response);
    }

    [Fact]
    public async Task RunAsync_ThrowsTimeout_WhenTheRunOutlivesTheWallClockLimit()
    {
        var spawner = new SleepingSpawner();
        var agent = new CodingAgentReviewAgent("codex", options: new CliOptions { Spawner = spawner },
            runTimeout: TimeSpan.FromMilliseconds(200));
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this repository", Directory.GetCurrentDirectory(), safety.Token));

        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task RunAsync_ThrowsAndStopsEarly_WhenOutputExceedsTheByteCap()
    {
        var spawner = new LoopingOutputSpawner();
        var agent = new CodingAgentReviewAgent("codex", options: new CliOptions { Spawner = spawner },
            maxOutputChars: 50);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(20));

        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this repository", Directory.GetCurrentDirectory(), safety.Token));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("output cap", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScriptedSpawner(IReadOnlyList<string> lines) : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var script = string.Join(" && ", lines.Select(line => $"echo {EscapeSingleQuoted(line)}"));
            var dummy = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", string.Join(" && ", lines.Select(l => $"echo {l}")) } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script } };
            dummy.RedirectStandardOutput = true;
            dummy.RedirectStandardError = true;
            dummy.RedirectStandardInput = true;
            dummy.UseShellExecute = false;
            dummy.CreateNoWindow = true;

            var process = Process.Start(dummy)!;
            return new CliSpawn(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError);
        }

        private static string EscapeSingleQuoted(string value) => "'" + value.Replace("'", "'\\''") + "'";
    }

    private sealed class SleepingSpawner : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var dummy = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "timeout /t 30" } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "sleep 30" } };
            dummy.RedirectStandardOutput = true;
            dummy.RedirectStandardError = true;
            dummy.RedirectStandardInput = true;
            dummy.UseShellExecute = false;
            dummy.CreateNoWindow = true;

            var process = Process.Start(dummy)!;
            return new CliSpawn(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError);
        }
    }

    /// <summary>Emits one large agent_message item, then sleeps: a well-behaved
    /// non-adversarial CLI never emits infinite output, so exceeding the cap once
    /// is enough to prove the run stops instead of waiting for the process to exit
    /// naturally.</summary>
    private sealed class LoopingOutputSpawner : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var text = new string('A', 500);
            var line = $"{{\"type\":\"item.completed\",\"item\":{{\"type\":\"agent_message\",\"text\":\"{text}\"}}}}";
            var script = $"echo '{line}' && sleep 30";
            var dummy = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", $"echo {line} && timeout /t 30" } }
                : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", script } };
            dummy.RedirectStandardOutput = true;
            dummy.RedirectStandardError = true;
            dummy.RedirectStandardInput = true;
            dummy.UseShellExecute = false;
            dummy.CreateNoWindow = true;

            var process = Process.Start(dummy)!;
            return new CliSpawn(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError);
        }
    }
}
