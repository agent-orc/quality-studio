using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Regression cover for the per-operation half of M-1. The 2026-08-18 review session found runs
/// that held their files "running" for 16 minutes with no reviewer CLI process attached, and which
/// still reported as successful reviews with 0 operations and 0 findings. Both bounds below must
/// hold for every reviewer operation. See docs/operations/security-concept/ finding N-01.
/// </summary>
public sealed class CodingAgentReviewAgentWatchdogTests
{
    // Codex is used deliberately: unlike the claude adapter it has no pre-spawn health probe, so
    // the fake spawner below is reached without needing a working CLI binary behind it.
    private const string CliType = "codex";

    [Fact]
    public async Task A_reviewer_that_spawns_and_then_goes_silent_is_killed_and_reported_as_failed()
    {
        // Every phase budget is tight, so a process that emits nothing trips the watchdog fast.
        var policy = new WatchdogPolicy
        {
            Enabled = true,
            WarmUpGraceSeconds = 0,
            QuietSeconds = 0.2,
            TickSeconds = 0.1,
            Budgets = Enum.GetValues<RunPhase>().ToDictionary(phase => phase, _ => new PhaseBudget(0.2, 0.5)),
        };
        using var spawner = new SilentProcessSpawner();
        var agent = new CodingAgentReviewAgent(
            CliType,
            options: new CliOptions { Spawner = spawner },
            watchdogPolicy: policy,
            attachTimeout: TimeSpan.FromSeconds(30));

        var failure = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));

        var aborted = Assert.IsType<ReviewAgentRunAbortedException>(failure.InnerException);
        Assert.Equal(CliType, aborted.CliType);
        Assert.Contains("watchdog", aborted.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reviewer_that_never_attaches_fails_within_the_attach_budget()
    {
        // The watchdog only tracks a run once the driver reports it started, so a spawn that never
        // returns is invisible to it. This is the gap the attach timeout closes.
        using var spawner = new BlockingProcessSpawner(TimeSpan.FromSeconds(30));
        var agent = new CodingAgentReviewAgent(
            CliType,
            options: new CliOptions { Spawner = spawner },
            attachTimeout: TimeSpan.FromMilliseconds(400));

        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        var timedOut = Assert.IsType<ReviewAgentAttachTimeoutException>(failure.InnerException);
        Assert.Equal(CliType, timedOut.CliType);
        // The point of the fix: the failure lands on the attach budget, not on the 30s the spawn
        // actually blocks for.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Attach timeout took {stopwatch.Elapsed.TotalSeconds:0.#}s; it must not wait for the blocked spawn.");
    }

    /// <summary>Spawns a real but permanently silent child process instead of the reviewer CLI.</summary>
    private sealed class SilentProcessSpawner : ICliProcessSpawner, IDisposable
    {
        private readonly List<Process> spawned = [];

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var silent = new ProcessStartInfo("sleep", "300")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = startInfo.WorkingDirectory,
            };
            var process = Process.Start(silent)!;
            lock (spawned) spawned.Add(process);
            return new CliSpawn(process, process.StandardInput.BaseStream,
                process.StandardOutput, process.StandardError);
        }

        public void Dispose()
        {
            lock (spawned)
            {
                foreach (var process in spawned)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    process.Dispose();
                }
            }
        }
    }

    /// <summary>Blocks inside the spawn call itself — the reviewer process never comes up at all.</summary>
    private sealed class BlockingProcessSpawner(TimeSpan block) : ICliProcessSpawner, IDisposable
    {
        private readonly CancellationTokenSource release = new();

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            // Blocking, not awaiting: this is what makes the first MoveNextAsync run synchronously
            // on the caller's thread, which the fix has to defend against.
            release.Token.WaitHandle.WaitOne(block);
            throw new InvalidOperationException("The reviewer process never started.");
        }

        public void Dispose()
        {
            release.Cancel();
            release.Dispose();
        }
    }
}
