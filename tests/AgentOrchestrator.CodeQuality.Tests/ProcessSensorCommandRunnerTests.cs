using System.Diagnostics;
using System.Globalization;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_CompletedCommandReturnsNormally()
    {
        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(
            "dotnet", ["--version"], Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_TimesOutAndKillsTheEntireProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The process-tree probe requires a POSIX shell.");

        var root = Directory.CreateTempSubdirectory("quality-studio-runner-timeout-").FullName;
        var pidFile = Path.Combine(root, "pids");
        int[] processIds = [];
        try
        {
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromMilliseconds(250));
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
                () => runner.RunAsync("/bin/sh", HangingCommand(pidFile), root, TestContext.Current.CancellationToken));

            stopwatch.Stop();
            processIds = await ReadProcessIdsAsync(pidFile);
            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
                $"The timed-out command took {stopwatch.Elapsed} to return.");
            await AssertProcessesExitAsync(processIds);
        }
        finally
        {
            KillProcesses(processIds);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationKillsTheEntireProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The process-tree probe requires a POSIX shell.");

        var root = Directory.CreateTempSubdirectory("quality-studio-runner-cancel-").FullName;
        var pidFile = Path.Combine(root, "pids");
        int[] processIds = [];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task<SensorCommandResult>? run = null;
        try
        {
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromMinutes(1));
            run = runner.RunAsync("/bin/sh", HangingCommand(pidFile), root, cancellation.Token);
            processIds = await ReadProcessIdsAsync(pidFile);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            await AssertProcessesExitAsync(processIds);
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
                catch (Exception)
                {
                    // The test intentionally cancels this command.
                }
            }

            KillProcesses(processIds);
            Directory.Delete(root, true);
        }
    }

    private static IReadOnlyList<string> HangingCommand(string pidFile) =>
    [
        "-c",
        "sleep 300 & child_pid=$!; printf '%s %s' \"$$\" \"$child_pid\" > \"$1\"; wait \"$child_pid\"",
        "quality-studio-process-runner-test",
        pidFile,
    ];

    private static async Task<int[]> ReadProcessIdsAsync(string pidFile)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(pidFile) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(pidFile), "The hanging command did not record its process IDs.");
        var contents = await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken);
        return contents.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static async Task AssertProcessesExitAsync(IEnumerable<int> processIds)
    {
        var ids = processIds.ToArray();
        var deadline = Stopwatch.StartNew();
        while (ids.Any(IsProcessAlive) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        Assert.All(ids, processId => Assert.False(IsProcessAlive(processId),
            $"Process {processId} survived command cleanup."));
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

    private static void KillProcesses(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds.Where(IsProcessAlive))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Best-effort cleanup for a failed assertion.
            }
        }
    }
}
