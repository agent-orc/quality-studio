namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalyzerProfileTests
{
    private static readonly AnalyzerProfileCatalog Enabled = new(commandProfilesEnabled: true);

    [Theory]
    [InlineData("/bin/sh -c \"env\"")]
    [InlineData("powershell.exe -Command Get-Content ~/.aws/credentials")]
    [InlineData("npx --no-install eslint")]
    public void RepositoryCommands_AreRejectedRegardlessOfShape(string command)
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["command"] = command };

        Assert.Equal(AnalyzerProfileCatalog.CommandRejectionReason, Enabled.Validate("eslint", configuration));
        Assert.Equal(AnalyzerProfileCatalog.CommandRejectionReason,
            Enabled.Resolve("eslint", configuration).RejectionReason);
    }

    [Fact]
    public void UnknownProfileId_IsRejectedAndListsTheHostOwnedAlternatives()
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "curl-anything" };

        var rejection = Enabled.Validate("eslint", configuration);

        Assert.NotNull(rejection);
        Assert.Contains("curl-anything", rejection, StringComparison.Ordinal);
        Assert.Contains("eslint-sarif", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericSarifSensor_HasNoExecutableProfileAtAll()
    {
        Assert.Empty(Enabled.For("sarif"));

        var rejection = Enabled.Validate("sarif",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "eslint-sarif" });

        Assert.Contains("can only ingest an existing report", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandProfiles_AreDisabledInTheDefaultCatalog()
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "eslint-sarif" };

        Assert.False(AnalyzerProfileCatalog.Disabled.CommandProfilesEnabled);
        Assert.Null(AnalyzerProfileCatalog.Disabled.Validate("eslint", configuration));
        Assert.Equal(AnalyzerProfileCatalog.CommandProfilesDisabledReason,
            AnalyzerProfileCatalog.Disabled.Resolve("eslint", configuration).RejectionReason);
    }

    [Fact]
    public void ConfigurationWithoutAProfile_IngestsAnExistingReport()
    {
        var resolution = Enabled.Resolve("roslyn",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reportPath"] = ".quality/roslyn.sarif" });

        Assert.False(resolution.IsRejected);
        Assert.Null(resolution.Profile);
    }

    [Fact]
    public void ResolvedProfile_KeepsTheHostOwnedExecutableAndSubstitutesConfinedPaths()
    {
        var resolution = Enabled.Resolve("roslyn",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "dotnet-build-sarif" });

        var profile = Assert.IsType<AnalyzerProfile>(resolution.Profile);
        Assert.Equal("dotnet", profile.Executable);
        Assert.DoesNotContain(profile.Arguments, argument => argument.Contains("&&", StringComparison.Ordinal));
    }
}
