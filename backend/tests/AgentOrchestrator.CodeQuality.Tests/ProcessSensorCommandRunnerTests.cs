using System.Diagnostics;

namespace AgentOrchestrator.CodeQuality.Tests;

[Trait("Category", "ToolBound")]
public sealed class ProcessSensorCommandRunnerTests
{

    [Fact]
    public async Task Default_output_limit_rejects_large_output_instead_of_returning_partial_evidence()
    {
        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10));

        var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() => runner.RunAsync(
            "node", ["-e", "process.stdout.write('x'.repeat(2_000_000))"],
            Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));

        Assert.Contains("output limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task Output_beyond_either_pipe_limit_is_unavailable_even_when_command_exits_successfully(string stream)
    {
        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10), maximumOutputCharacters: 128);

        var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() => runner.RunAsync(
            "node", ["-e", $"process.{stream}.write('x'.repeat(129))"],
            Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken));

        Assert.Contains("output limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_exactly_at_each_pipe_limit_remains_complete()
    {
        var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10), maximumOutputCharacters: 128);

        var result = await runner.RunAsync("node",
            ["-e", "process.stdout.write('o'.repeat(128)); process.stderr.write('e'.repeat(128))"],
            Directory.GetCurrentDirectory(), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string('o', 128), result.StandardOutput);
        Assert.Equal(new string('e', 128), result.StandardError);
    }

    [Fact]
    public async Task Output_limit_kills_the_process_tree_and_reports_overflow()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-process-output-").FullName;
        IReadOnlyList<int> processIds = [];
        try
        {
            var pidFile = Path.Combine(root, "pids.txt");
            var runner = new ProcessSensorCommandRunner(TimeSpan.FromSeconds(10), maximumOutputCharacters: 128);
            var script = "const fs = require('node:fs'); const {spawn} = require('node:child_process'); " +
                         "const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], " +
                         "{stdio:'ignore', windowsHide:true}); " +
                         "fs.writeFileSync(process.argv[1], process.pid + '\\n' + child.pid + '\\n'); " +
                         "setInterval(() => process.stdout.write('x'.repeat(4096)), 20);";

            var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(() => runner.RunAsync(
                "node", ["-e", script, pidFile], root, TestContext.Current.CancellationToken));

            processIds = await ReadProcessIdsAsync(pidFile, TestContext.Current.CancellationToken);
            Assert.Contains("output limit", exception.Message, StringComparison.Ordinal);
            await AssertProcessesExitedAsync(processIds, TestContext.Current.CancellationToken);
        }
        finally
        {
            // Also clean up a descendant when this regression runs against a broken implementation.
            foreach (var processId in processIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
            Directory.Delete(root, true);
        }
    }

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
            Assert.Skip("The process-tree fixture requires POSIX shell process semantics unavailable on Windows.");
    }
}
