namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Config-contract coverage for static-analysis dossier slice S4
/// (docs/operations/static-analysis/index.html, section 08): CA1502/CA1505 must stay wired as
/// warnings scoped to production code, with thresholds recorded in one place.
/// </summary>
public sealed class ComplexityThresholdConfigurationTests
{
    private static readonly string RepositoryRoot = RepositoryTestContext.FindRepositoryRoot();

    [Fact]
    public void Root_editorconfig_enables_CA1502_and_CA1505_as_warnings_for_src_only()
    {
        var editorConfig = File.ReadAllText(Path.Combine(RepositoryRoot, ".editorconfig"));

        Assert.Contains("[src/**.cs]", editorConfig);
        Assert.Contains("dotnet_diagnostic.CA1502.severity = warning", editorConfig);
        Assert.Contains("dotnet_diagnostic.CA1505.severity = warning", editorConfig);
    }

    [Fact]
    public void Root_code_metrics_config_pins_CA1502_and_CA1505_thresholds()
    {
        var config = File.ReadAllText(Path.Combine(RepositoryRoot, "CodeMetricsConfig.txt"));

        Assert.Contains("CA1502: 25", config);
        Assert.Contains("CA1505: 10", config);
    }

    [Fact]
    public void Directory_Build_props_keeps_complexity_warnings_from_breaking_the_build()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", props);
        Assert.Contains("WarningsNotAsErrors", props);
        Assert.Contains("CA1502", props);
        Assert.Contains("CA1505", props);
        Assert.Contains("CodeMetricsConfig.txt", props);
    }

    [Fact]
    public void Directory_Build_props_scopes_code_metrics_config_to_non_test_projects()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains("IsTestProject", props);
        Assert.Contains("AdditionalFiles", props);
        Assert.Contains("NoWarn", props);
    }
}
