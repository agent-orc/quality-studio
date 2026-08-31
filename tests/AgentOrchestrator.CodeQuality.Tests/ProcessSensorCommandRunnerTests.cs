using System.Diagnostics;
using System.Globalization;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task Timeout_kills_the_entire_process_tree()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The process-tree fixture uses Linux shell commands.");
        var pidFile = Path.Combine(Path.GetTempPath(), $"quality-studio-timeout-{Guid.NewGuid():N}.pid");
        var stopwatch = Stopwatch.StartNew();
        int? childPid = null;
        try
        {
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(1));
            var run = runner.RunAsync(
                "/bin/sh", ShellCommand(pidFile), Directory.GetCurrentDirectory(),
                TestContext.Current.CancellationToken);
            childPid = await ReadChildPidAsync(pidFile);

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() => run);

            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"The timed-out command returned after {stopwatch.Elapsed}.");
            await AssertProcessStoppedAsync(childPid.Value);
        }
        finally
        {
            CleanupProcess(childPid);
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_entire_process_tree()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "The process-tree fixture uses Linux shell commands.");
        var pidFile = Path.Combine(Path.GetTempPath(), $"quality-studio-cancellation-{Guid.NewGuid():N}.pid");
        int? childPid = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(30));
            var run = runner.RunAsync(
                "/bin/sh", ShellCommand(pidFile), Directory.GetCurrentDirectory(), cancellation.Token);
            childPid = await ReadChildPidAsync(pidFile);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await AssertProcessStoppedAsync(childPid.Value);
        }
        finally
        {
            CleanupProcess(childPid);
            File.Delete(pidFile);
        }
    }

    private static IReadOnlyList<string> ShellCommand(string pidFile) =>
    [
        "-c",
        "sleep 30 & child=$!; printf '%s' \"$child\" > \"$1\"; wait \"$child\"",
        "process-runner-test",
        pidFile,
    ];

    private static async Task<int> ReadChildPidAsync(string pidFile)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                if (int.TryParse(await File.ReadAllTextAsync(
                        pidFile, TestContext.Current.CancellationToken),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }
            catch (FileNotFoundException)
            {
                // The shell has not written its child PID yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The process-tree fixture did not publish its child PID.");
    }

    private static async Task AssertProcessStoppedAsync(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (!IsProcessRunning(pid)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        Assert.False(IsProcessRunning(pid), $"Child process {pid} survived command cleanup.");
    }

    private static bool IsProcessRunning(int pid)
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

    private static void CleanupProcess(int? pid)
    {
        if (pid is null) return;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The assertion target already exited.
        }
    }
}
