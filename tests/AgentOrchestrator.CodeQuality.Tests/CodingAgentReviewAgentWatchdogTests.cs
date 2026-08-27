using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class CodingAgentReviewAgentWatchdogTests
{
    [Fact]
    public async Task RunAsync_FailsLoudlyWhenTheReviewerAttachesButGoesSilent()
    {
        var options = new CliOptions { Spawner = new SilentProcessSpawner() };
        var policy = new WatchdogPolicy
        {
            WarmUpGraceSeconds = 0,
            TickSeconds = 1,
            Budgets = new Dictionary<RunPhase, PhaseBudget> { [RunPhase.Spawning] = new(0, 1) },
        };
        var agent = new CodingAgentReviewAgent(
            "codex", watchdogPolicy: policy, attachTimeout: TimeSpan.FromSeconds(30), options: options);
        var workingDirectory = Directory.CreateTempSubdirectory().FullName;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(() =>
                agent.RunAsync("review this file", workingDirectory, TestContext.Current.CancellationToken));
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Expected the watchdog to fail a silent reviewer quickly; took {stopwatch.Elapsed}.");
            Assert.Contains("watchdog", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no activity", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workingDirectory, true);
        }
    }

    // Spawns a real, trivial, permanently-silent process instead of the requested CLI —
    // the same observable shape as a reviewer that attached but never produced output.
    private sealed class SilentProcessSpawner : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var silentStartInfo = new ProcessStartInfo("sleep")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            silentStartInfo.ArgumentList.Add("60");
            var process = new Process { StartInfo = silentStartInfo, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError);
        }
    }
}
