using System.Diagnostics;
using CodingAgentRunner.Abstractions;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Regression coverage for the dossier's F-02/R-02 finding: the review agent must
/// run under an isolated per-run CLI home, not the operator's shared, signed-in
/// session/settings/memory. See docs/operations/security/index.html §05 F-02.
/// </summary>
public sealed class ReviewAgentContextModeTests
{
    [Fact]
    public async Task RunAsync_IsolatesTheCliHome_InsteadOfReusingTheOperatorsSharedContext()
    {
        var spawner = new RecordingSpawner();
        var agent = new CodingAgentReviewAgent("codex", options: new CliOptions { Spawner = spawner });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("review this repository", Directory.GetCurrentDirectory(), cts.Token));

        Assert.NotNull(spawner.Captured);
        Assert.True(spawner.Captured!.Environment.TryGetValue("CODEX_HOME", out var codexHome));
        Assert.False(string.IsNullOrWhiteSpace(codexHome));

        // The clean-context temp home lives under a well-known per-run directory,
        // distinct from the operator's real "~/.codex". A "shared" run never sets
        // CODEX_HOME at all, so this also fails before the fix (no key present).
        // (The engine tears the temp home down as soon as the cancelled run's
        // teardown runs, so this asserts on the path shape, not its survival.)
        var isolatedHomeRoot = Path.Combine(Path.GetTempPath(), "coding-agent-runner-clean-context");
        Assert.StartsWith(isolatedHomeRoot, codexHome);
    }

    private sealed class RecordingSpawner : ICliProcessSpawner
    {
        public ProcessStartInfo? Captured { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            Captured = startInfo;

            // A real, safely-killable child that never produces output and outlives
            // the test's cancellation window, so the engine's read loop blocks until
            // RunAsync's cancellation tears it down — we only need to observe the
            // environment the engine built before spawning, not drive a real CLI.
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
}
