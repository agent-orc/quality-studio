namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AllowlistedSensorCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_DelegatesWhenExecutableIsAllowed()
    {
        var inner = new RecordingRunner();
        var runner = new AllowlistedSensorCommandRunner(inner, ["dotnet", "npx"]);

        var result = await runner.RunAsync("dotnet", ["--version"], "/repo", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("dotnet", inner.LastExecutable);
    }

    [Fact]
    public async Task RunAsync_IsCaseInsensitiveForAllowedNames()
    {
        var inner = new RecordingRunner();
        var runner = new AllowlistedSensorCommandRunner(inner, ["dotnet"]);

        await runner.RunAsync("DOTNET", [], "/repo", TestContext.Current.CancellationToken);

        Assert.Equal("DOTNET", inner.LastExecutable);
    }

    [Theory]
    [InlineData("/bin/sh")]
    [InlineData("powershell.exe")]
    [InlineData("sh")]
    [InlineData("bash")]
    [InlineData("cmd")]
    public async Task RunAsync_RejectsExecutablesNotOnTheAllowlist(string executable)
    {
        var inner = new RecordingRunner();
        var runner = new AllowlistedSensorCommandRunner(inner, ["dotnet", "npx", "npm"]);

        var exception = await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
            () => runner.RunAsync(executable, ["-c", "echo hi"], "/repo", TestContext.Current.CancellationToken));

        Assert.Contains(executable, exception.Message, StringComparison.Ordinal);
        Assert.False(inner.WasInvoked);
    }

    [Theory]
    [InlineData("./dotnet")]
    [InlineData("../../../bin/dotnet")]
    [InlineData(@"C:\Windows\System32\dotnet.exe")]
    public async Task RunAsync_RejectsPathQualifiedExecutablesEvenWithAnAllowedBaseName(string executable)
    {
        var inner = new RecordingRunner();
        var runner = new AllowlistedSensorCommandRunner(inner, ["dotnet"]);

        await Assert.ThrowsAsync<SecurityScannerUnavailableException>(
            () => runner.RunAsync(executable, [], "/repo", TestContext.Current.CancellationToken));

        Assert.False(inner.WasInvoked);
    }

    private sealed class RecordingRunner : ISensorCommandRunner
    {
        public bool WasInvoked { get; private set; }
        public string? LastExecutable { get; private set; }

        public Task<SensorCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            LastExecutable = executable;
            return Task.FromResult(new SensorCommandResult(0, string.Empty, string.Empty));
        }
    }
}
