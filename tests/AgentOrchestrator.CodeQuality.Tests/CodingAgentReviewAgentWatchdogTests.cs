using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class CodingAgentReviewAgentWatchdogTests
{
    [Fact]
    public async Task RunAsync_FailsBoundedTime_WhenReviewerProcessGoesSilent()
    {
        var tinyBudget = new PhaseBudget(SuspiciousSeconds: 0.02, HungSeconds: 0.05);
        var policy = new WatchdogPolicy
        {
            WarmUpGraceSeconds = 0,
            QuietSeconds = 0,
            TickSeconds = 0.02,
            Budgets = Enum.GetValues<RunPhase>().ToDictionary(phase => phase, _ => tinyBudget),
        };
        var agent = new CodingAgentReviewAgent(
            "codex",
            options: new CliOptions { Spawner = new SilentProcessSpawner() },
            watchdogPolicy: policy,
            attachTimeout: TimeSpan.FromSeconds(10));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Contains("watchdog", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"watchdog should have failed the run well under the 10s attach timeout; took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_FailsBoundedTime_WhenReviewerProcessNeverAttaches()
    {
        // RunWatchdog only starts tracking a run once OnStarted fires, so a spawn
        // mechanism that never returns is a gap it structurally cannot cover; the
        // attach-timeout is what bounds this case instead.
        var agent = new CodingAgentReviewAgent(
            "codex",
            options: new CliOptions { Spawner = new NeverAttachingSpawner(TimeSpan.FromSeconds(5)) },
            attachTimeout: TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Contains("attach timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"attach timeout should have failed the run well under the simulated 5s spawn hang; took {stopwatch.Elapsed}");
    }

    /// <summary>Spawns a real process that starts (OnStarted fires) but never produces output.</summary>
    private sealed class SilentProcessSpawner : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("sleep 60");
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process, Stream.Null, process.StandardOutput, process.StandardError);
        }
    }

    /// <summary>Simulates a spawn mechanism that blocks for <paramref name="delay"/> before ever launching a process.</summary>
    private sealed class NeverAttachingSpawner(TimeSpan delay) : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            Thread.Sleep(delay);
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exit 0");
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process, Stream.Null, process.StandardOutput, process.StandardError);
        }
    }
}
