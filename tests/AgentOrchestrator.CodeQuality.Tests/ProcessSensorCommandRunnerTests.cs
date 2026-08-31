using System.Diagnostics;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ProcessSensorCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsCompletedProcessOutput()
    {
        if (!OperatingSystem.IsLinux()) return;

        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(5));

        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "printf standard-output; printf standard-error >&2; exit 7"],
            Directory.GetCurrentDirectory(),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("standard-output", result.StandardOutput);
        Assert.Equal("standard-error", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_TimesOutAndKillsProcess()
    {
        if (!OperatingSystem.IsLinux()) return;

        var directory = Directory.CreateTempSubdirectory("quality-studio-process-runner-").FullName;
        try
        {
            var pidPath = Path.Combine(directory, "process.pid");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromMilliseconds(500));
            var stopwatch = Stopwatch.StartNew();

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() =>
                runner.RunAsync(
                    "/bin/sh",
                    ["-c", "echo $$ > \"$1\"; exec sleep 30", "process-runner-test", pidPath],
                    directory,
                    TestContext.Current.CancellationToken));

            stopwatch.Stop();
            Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
            Assert.False(IsProcessAlive(await ReadPidAsync(pidPath)));
        }
        finally
        {
            TestDirectory.Delete(directory);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationKillsEntireProcessTree()
    {
        if (!OperatingSystem.IsLinux()) return;

        var directory = Directory.CreateTempSubdirectory("quality-studio-process-runner-").FullName;
        using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task command = Task.CompletedTask;
        try
        {
            var parentPidPath = Path.Combine(directory, "parent.pid");
            var childPidPath = Path.Combine(directory, "child.pid");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(30));
            command = runner.RunAsync(
                "/bin/sh",
                [
                    "-c",
                    "echo $$ > \"$1\"; sleep 30 & child=$!; echo $child > \"$2\"; wait",
                    "process-runner-test",
                    parentPidPath,
                    childPidPath,
                ],
                directory,
                cancellationSource.Token);

            await WaitForFileAsync(childPidPath);
            var parentPid = await ReadPidAsync(parentPidPath);
            var childPid = await ReadPidAsync(childPidPath);
            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            Assert.False(IsProcessAlive(parentPid));
            Assert.False(IsProcessAlive(childPid));
        }
        finally
        {
            cancellationSource.Cancel();
            await ObserveCancellationAsync(command);
            TestDirectory.Delete(directory);
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"The child process did not write '{path}'.");
    }

    private static async Task<int> ReadPidAsync(string path) =>
        int.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

    private static bool IsProcessAlive(int pid)
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

    private static async Task ObserveCancellationAsync(Task command)
    {
        try
        {
            await command.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
