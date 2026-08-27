namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class SecurityProvisionCommandTests
{
    [Fact]
    public async Task Provision_reports_the_verified_pinned_binary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::QualityCli.RunSecurityProvisionAsync(
            _ => Task.FromResult("/tools/gitleaks"), output, error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("gitleaks 8.24.2 | /tools/gitleaks", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task Provision_keeps_an_unavailable_tool_distinct_from_a_pass()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await global::QualityCli.RunSecurityProvisionAsync(
            _ => Task.FromException<string>(new SecurityScannerUnavailableException("download failed")),
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("quality security provision failed: download failed", error.ToString(),
            StringComparison.Ordinal);
    }
}
