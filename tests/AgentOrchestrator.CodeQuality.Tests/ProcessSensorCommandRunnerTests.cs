using System.Diagnostics;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task Completed_command_returns_exit_code_and_redirected_output()
    {
        SkipUnlessPosix();
        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "printf 'standard output'; printf 'standard error' >&2; exit 7"],
            Directory.GetCurrentDirectory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("standard output", result.StandardOutput);
        Assert.Equal("standard error", result.StandardError);
    }

    [Fact]
    public async Task Timeout_kills_the_process_tree_and_returns_promptly()
    {
        SkipUnlessPosix();
        var root = Directory.CreateTempSubdirectory("quality-studio-process-timeout-").FullName;
        try
        {
            var pidFile = Path.Combine(root, "pids.txt");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromMilliseconds(250));
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() =>
                runner.RunAsync("/bin/sh", HangingCommand(pidFile), root, CancellationToken.None));

            stopwatch.Stop();
            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Timed-out command returned after {stopwatch.Elapsed}.");
            var processIds = await ReadProcessIdsAsync(pidFile, TestContext.Current.CancellationToken);
            await AssertProcessesExitedAsync(processIds, TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Cancellation_kills_the_process_tree()
    {
        SkipUnlessPosix();
        var root = Directory.CreateTempSubdirectory("quality-studio-process-cancel-").FullName;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        Task<SensorCommandResult>? run = null;
        try
        {
            var pidFile = Path.Combine(root, "pids.txt");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(30));
            run = runner.RunAsync("/bin/sh", HangingCommand(pidFile), root, cancellation.Token);
            var processIds = await ReadProcessIdsAsync(pidFile, TestContext.Current.CancellationToken);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await AssertProcessesExitedAsync(processIds, TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            if (run is not null)
            {
                try
                {
                    await run;
                }
                catch (Exception exception) when (exception is OperationCanceledException or SecurityScannerUnavailableException)
                {
                }
            }

            Directory.Delete(root, true);
        }
    }

    private static string[] HangingCommand(string pidFile) =>
    [
        "-c",
        "sleep 30 & child=$!; printf '%s\\n%s\\n' \"$$\" \"$child\" > \"$1\"; wait \"$child\"",
        "process-sensor-test",
        pidFile,
    ];

    private static async Task<IReadOnlyList<int>> ReadProcessIdsAsync(
        string pidFile,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(pidFile))
            {
                var lines = await File.ReadAllLinesAsync(pidFile, cancellationToken);
                if (lines.Length == 2 && lines.All(line => int.TryParse(line, out _)))
                {
                    return lines.Select(int.Parse).ToArray();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException("The child process did not publish its process IDs.");
    }

    private static async Task AssertProcessesExitedAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && processIds.Any(IsProcessAlive))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        Assert.All(processIds, processId =>
            Assert.False(IsProcessAlive(processId), $"Process {processId} survived command cleanup."));
    }

    private static bool IsProcessAlive(int processId)
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

    private static void SkipUnlessPosix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The process-tree fixture uses the POSIX shell available on runner hosts.");
        }
    }
}
