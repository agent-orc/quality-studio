namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Guards the CA1502/CA1505 complexity-threshold wiring from
/// docs/operations/static-analysis/index.html slice S4: the rules must be enabled for src/ only,
/// configured with an explicit threshold, and excluded from TreatWarningsAsErrors so a complexity
/// finding never blocks the build.
/// </summary>
public sealed class ComplexityThresholdConfigurationTests
{
    [Fact]
    public void Root_editorconfig_enables_CA1502_and_CA1505_for_src_only()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var editorConfig = File.ReadAllText(Path.Combine(root, ".editorconfig"));

        Assert.Contains("[src/**.cs]", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.CA1502.severity = warning", editorConfig, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.CA1505.severity = warning", editorConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeMetricsConfig_declares_explicit_thresholds()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var config = File.ReadAllText(Path.Combine(root, "CodeMetricsConfig.txt"));

        Assert.Contains("CA1502: 25", config, StringComparison.Ordinal);
        Assert.Contains("CA1505: 10", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_Build_props_wires_the_config_file_and_keeps_the_build_green()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.Contains("<AdditionalFiles Include=\"$(MSBuildThisFileDirectory)CodeMetricsConfig.txt\" />",
            props, StringComparison.Ordinal);
        Assert.Contains("<WarningsNotAsErrors>$(WarningsNotAsErrors);CA1502;CA1505</WarningsNotAsErrors>",
            props, StringComparison.Ordinal);
    }
}
