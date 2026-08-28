namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Regression coverage for the dossier's S1 "enforce wall-clock … and output-byte
/// … quotas" exit criterion (docs/operations/security/index.html §08 S1): before
/// this change, <see cref="ProcessSensorCommandRunner.RunAsync"/> awaited
/// <c>WaitForExitAsync</c> with no timeout and buffered stdout/stderr with an
/// unbounded <c>ReadToEndAsync</c>.
/// </summary>
public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsOutput_ForAnOrdinaryCommand()
    {
        var runner = new ProcessSensorCommandRunner();

        var result = OperatingSystem.IsWindows()
            ? await runner.RunAsync("cmd.exe", ["/c", "echo hello"], Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken)
            : await runner.RunAsync("/bin/sh", ["-c", "echo hello"], Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_ThrowsAndKillsTheProcess_WhenItOutlivesTheWallClockLimit()
    {
        var runner = new ProcessSensorCommandRunner(commandTimeout: TimeSpan.FromMilliseconds(200));
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(10));

        var command = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "timeout /t 30"])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", "sleep 30"]);

        var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
            () => runner.RunAsync(command.Item1, command.Item2, Directory.GetCurrentDirectory(), safety.Token));

        Assert.Contains("wall-clock", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ThrowsAndStopsEarly_WhenOutputExceedsTheByteCap()
    {
        var runner = new ProcessSensorCommandRunner(maxOutputBytes: 64);
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(20));

        var command = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", "for /l %i in (1,1,100000) do @echo AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", "while true; do echo AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA; done"]);

        var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
            () => runner.RunAsync(command.Item1, command.Item2, Directory.GetCurrentDirectory(), safety.Token));

        Assert.Contains("output cap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
