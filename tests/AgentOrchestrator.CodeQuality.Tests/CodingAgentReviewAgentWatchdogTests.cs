using System.Diagnostics;
using AgentOrchestrator.CodeQuality;
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// QS-93 M-1: a reviewer run whose CLI process dies or never attaches must fail loudly
/// within a bounded time instead of holding its work item "running" forever (the
/// 2026-08-18 dossier finding: 7 claude runs at 0 operations/0 findings/0 tokens, one
/// held "running" for 16 minutes with no reviewer process attached). Both tests spawn a
/// real subprocess via a decorated <see cref="ICliProcessSpawner"/> so they exercise the
/// actual CliRunEngine spawn path, not a fake driver. <c>cliType: "codex"</c> is used
/// deliberately: Codex has no <c>EnsureHealthy</c> pre-spawn check (Claude does), so the
/// fake spawner is reached without needing a real CLI binary on PATH.
/// </summary>
public sealed class CodingAgentReviewAgentWatchdogTests
{
    private static WatchdogPolicy FastPolicy => new()
    {
        WarmUpGraceSeconds = 0,
        TickSeconds = 0.05,
        QuietSeconds = 0.05,
        Budgets = new Dictionary<RunPhase, PhaseBudget>
        {
            [RunPhase.Spawning] = new(0.05, 0.15),
        },
    };

    // CliProcessSpawner.Decorate (the published helper for this) isn't in the installed
    // 0.7.0 package yet, so this mirrors DefaultCliProcessSpawner's Linux path directly:
    // run `prepare` against the engine's already-built ProcessStartInfo, then really spawn it.
    private sealed class DecoratingSpawner(Action<ProcessStartInfo> prepare) : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            prepare(startInfo);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(
                process,
                startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
                process.StandardOutput,
                process.StandardError);
        }
    }

    private static ICliProcessSpawner DecoratedSpawn(Action<ProcessStartInfo> prepare) =>
        new DecoratingSpawner(prepare);

    [Fact(Timeout = 10_000)]
    public async Task RunAsync_DeadReviewer_WatchdogStopsItWithinBudget()
    {
        // The CLI process spawns and stays completely silent (no protocol frames, no
        // exit) — exactly the "no reviewer CLI process attached" symptom, except here
        // the process DID attach; it just never says anything again. RunWatchdog's
        // Spawning-phase silence budget must catch this and auto-stop it.
        var options = new CliOptions
        {
            Spawner = DecoratedSpawn(psi =>
            {
                psi.FileName = "/bin/sleep";
                psi.ArgumentList.Clear();
                psi.ArgumentList.Add("999");
            }),
        };
        var agent = new CodingAgentReviewAgent(
            cliType: "codex", options: options, attachTimeout: TimeSpan.FromSeconds(5), watchdogPolicy: FastPolicy);

        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));

        Assert.Contains("watchdog", exception.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 10_000)]
    public async Task RunAsync_SpawnNeverAttaches_FailsAtAttachTimeoutNotAtTheBlockingSpawn()
    {
        // Simulates a hang BEFORE the driver's OnStarted ever fires (the actual gap in
        // the dossier's finding) via a spawner whose synchronous prefix blocks far
        // longer than the configured attach timeout. If the attach-timeout wrapping
        // didn't force the first MoveNextAsync() onto its own thread, this synchronous
        // block would starve Task.WhenAny and the test would take ~3s instead of ~200ms.
        var spawnGate = new ManualResetEventSlim(false);
        var options = new CliOptions
        {
            Spawner = DecoratedSpawn(psi =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                spawnGate.Set();
                psi.FileName = "/bin/echo";
                psi.ArgumentList.Clear();
                psi.ArgumentList.Add("hi");
            }),
        };
        var agent = new CodingAgentReviewAgent(
            cliType: "codex", options: options, attachTimeout: TimeSpan.FromMilliseconds(200));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<ReviewAgentRunException>(
            () => agent.RunAsync("review this", Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("did not attach", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"expected the attach-timeout ({200}ms) to win the race, but RunAsync took {stopwatch.Elapsed}");

        // The blocked spawn eventually completes on its own background continuation;
        // give it a chance to settle so the process isn't leaked past the test.
        Assert.True(spawnGate.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
