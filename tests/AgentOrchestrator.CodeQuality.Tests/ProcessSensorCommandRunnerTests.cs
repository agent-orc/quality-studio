using System.Diagnostics;
using System.Globalization;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_TimesOutAndKillsTheEntireProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"quality-studio-child-{Guid.NewGuid():N}.pid");
        try
        {
            var command = HangingCommand(pidFile);
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() =>
                runner.RunAsync(command.Executable, command.Arguments, Directory.GetCurrentDirectory(),
                    TestContext.Current.CancellationToken));

            stopwatch.Stop();
            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"The timed-out command took {stopwatch.Elapsed} to return.");
            await AssertChildExitedAsync(await ReadPidAsync(pidFile));
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationKillsTheEntireProcessTree()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"quality-studio-child-{Guid.NewGuid():N}.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            var command = HangingCommand(pidFile);
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(30));
            var running = runner.RunAsync(
                command.Executable, command.Arguments, Directory.GetCurrentDirectory(), cancellation.Token);
            var childPid = await WaitForPidAsync(pidFile);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await AssertChildExitedAsync(childPid);
        }
        finally
        {
            cancellation.Cancel();
            File.Delete(pidFile);
        }
    }

    private static Command HangingCommand(string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            const string script =
                "$child = Start-Process -FilePath $env:ComSpec -ArgumentList '/c','ping -n 31 127.0.0.1 > nul' -PassThru; " +
                "Set-Content -LiteralPath $args[0] -Value $child.Id; Wait-Process -Id $child.Id";
            return new Command("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script, pidFile]);
        }

        return new Command("/bin/sh", ["-c", "sleep 30 & echo $! > \"$1\"; wait", "process-sensor-test", pidFile]);
    }

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(pidFile))
            {
                return await ReadPidAsync(pidFile);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The child process did not publish its PID.");
    }

    private static async Task<int> ReadPidAsync(string pidFile)
    {
        var value = await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken);
        return int.Parse(value.Trim(), CultureInfo.InvariantCulture);
    }

    private static async Task AssertChildExitedAsync(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && IsAlive(pid))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        Assert.False(IsAlive(pid), $"Child process {pid} survived command termination.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record Command(string Executable, IReadOnlyList<string> Arguments);
}
