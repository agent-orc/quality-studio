using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Regression coverage for N-01: a reviewer CLI process that spawns and then never
/// produces another byte of output (no crash, no exit, no cancellation-aware behavior)
/// previously hung <see cref="CodingAgentReviewAgent.RunAsync"/> forever. The fix
/// attaches the CodingAgentRunner package's own (previously unused) watchdog so a dead
/// reviewer is killed and surfaced as a typed <see cref="ReviewAgentRunTimeoutException"/>
/// within a bounded time instead of hanging indefinitely.
/// </summary>
public sealed class DeadReviewerWatchdogTests
{
    [Fact]
    public async Task A_reviewer_process_that_never_produces_output_is_reclaimed_within_its_watchdog_budget()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake dead-CLI fixture is a POSIX shell script.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var scriptPath = Path.Combine(Path.GetTempPath(), $"quality-dead-cli-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\nexec sleep 3600\n", cancellationToken);
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // Codex has no pre-spawn health probe, so pointing CodexPath at the fixture is
            // enough to make it "the reviewer CLI" without also faking a --version probe.
            var agent = new CodingAgentReviewAgent(
                "codex",
                options: new CliOptions { CodexPath = scriptPath },
                watchdogPolicy: new WatchdogPolicy
                {
                    WarmUpGraceSeconds = 0,
                    QuietSeconds = 0,
                    Budgets = new Dictionary<RunPhase, PhaseBudget>
                    {
                        [RunPhase.Spawning] = new PhaseBudget(SuspiciousSeconds: 0.1, HungSeconds: 0.3),
                    },
                });

            var stopwatch = Stopwatch.StartNew();
            var exception = await Assert.ThrowsAsync<ReviewAgentRunTimeoutException>(
                () => agent.RunAsync("review this", Path.GetTempPath(), cancellationToken));
            stopwatch.Stop();

            Assert.Contains("watchdog", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Expected the watchdog to reclaim the dead process quickly; took {stopwatch.Elapsed}.");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}
