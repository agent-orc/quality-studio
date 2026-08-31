using System.Diagnostics;
using System.Globalization;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task Completed_command_returns_its_exit_code_and_output()
    {
        if (OperatingSystem.IsWindows()) return;

        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "printf 'standard output'; printf 'standard error' >&2; exit 7"],
            Directory.GetCurrentDirectory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("standard output", result.StandardOutput);
        Assert.Equal("standard error", result.StandardError);
        Assert.Equal(TimeSpan.FromMinutes(5), ProcessSensorCommandRunner.DefaultTimeout);
    }

    [Fact]
    public async Task Timeout_kills_the_entire_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("quality-studio-runner-timeout-").FullName;
        int? childProcessId = null;
        try
        {
            var pidFile = Path.Combine(root, "child.pid");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(1));
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
                () => RunHangingTreeAsync(
                    runner, root, pidFile, TestContext.Current.CancellationToken));

            stopwatch.Stop();
            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"The timed-out command returned after {stopwatch.Elapsed}.");
            childProcessId = await ReadProcessIdAsync(pidFile);
            await AssertProcessExitedAsync(childProcessId.Value);
        }
        finally
        {
            KillIfRunning(childProcessId);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Caller_cancellation_kills_the_entire_process_tree()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("quality-studio-runner-cancellation-").FullName;
        int? childProcessId = null;
        try
        {
            var pidFile = Path.Combine(root, "child.pid");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            var running = RunHangingTreeAsync(runner, root, pidFile, cancellation.Token);
            childProcessId = await ReadProcessIdAsync(pidFile);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await AssertProcessExitedAsync(childProcessId.Value);
        }
        finally
        {
            KillIfRunning(childProcessId);
            Directory.Delete(root, true);
        }
    }

    private static Task<SensorCommandResult> RunHangingTreeAsync(
        ProcessSensorCommandRunner runner,
        string workingDirectory,
        string pidFile,
        CancellationToken cancellationToken = default)
    {
        var script = $"sleep 30 & child=$!; printf '%s' \"$child\" > {ShellQuote(pidFile)}; wait \"$child\"";
        return runner.RunAsync("/bin/sh", ["-c", script], workingDirectory, cancellationToken);
    }

    private static async Task<int> ReadProcessIdAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(path))
            {
                var value = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                {
                    return processId;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException("The hanging child did not report its process ID.");
    }

    private static async Task AssertProcessExitedAsync(int processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && IsRunning(processId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }

        Assert.False(IsRunning(processId), $"Child process {processId} survived runner cleanup.");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfRunning(int? processId)
    {
        if (processId is null) return;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // The process already exited.
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
