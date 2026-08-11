namespace AgentOrchestrator.CodeQuality.Tests;

[CollectionDefinition("QualityCliConsole", DisableParallelization = true)]
public sealed class QualityCliConsoleCollection;

[Collection("QualityCliConsole")]
public sealed class QualityCliContractTests
{
    [Fact]
    public async Task Security_help_lists_provision_and_scan_commands()
    {
        using var output = new StringWriter();
        var originalOutput = Console.Out;
        Console.SetOut(output);
        int exitCode;
        try
        {
            exitCode = await global::QualityCli.RunAsync(["security", "--help"]);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        Assert.Equal(0, exitCode);
        Assert.Contains("quality security provision", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("quality security scan", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Security_provision_rejects_unexpected_arguments_before_network_access()
    {
        using var error = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(error);
        int exitCode;
        try
        {
            exitCode = await global::QualityCli.RunAsync(["security", "provision", "unexpected"]);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Equal(2, exitCode);
        Assert.Contains("does not accept arguments", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown security command", error.ToString(), StringComparison.Ordinal);
    }
}
