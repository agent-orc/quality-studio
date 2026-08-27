namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Config-contract tests for the static-analysis dossier's S4 slice
/// (docs/operations/static-analysis/index.html#slices): CA1502/CA1505 are enabled
/// as warnings for production .NET code only, and cannot become build errors.
/// </summary>
public sealed class ComplexityThresholdConfigurationTests
{
    private static string RepositoryRoot => RepositoryTestContext.FindRepositoryRoot();

    [Fact]
    public void Root_editorconfig_enables_complexity_rules_as_warnings_for_src_only()
    {
        var path = Path.Combine(RepositoryRoot, ".editorconfig");
        Assert.True(File.Exists(path), $"Expected a root .editorconfig at {path}.");

        var content = File.ReadAllText(path);
        Assert.Contains("root = true", content);
        Assert.Contains("[src/**.cs]", content);
        Assert.Contains("dotnet_diagnostic.CA1502.severity = warning", content);
        Assert.Contains("dotnet_diagnostic.CA1505.severity = warning", content);
    }

    [Fact]
    public void CodeMetricsConfig_declares_documented_default_thresholds()
    {
        var path = Path.Combine(RepositoryRoot, "src", "CodeMetricsConfig.txt");
        Assert.True(File.Exists(path), $"Expected {path} for CA1502/CA1505 thresholds.");

        var lines = File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        Assert.Contains("CA1502: 25", lines);
        Assert.Contains("CA1505: 10", lines);
    }

    [Fact]
    public void Src_Directory_Build_Props_wires_metrics_config_and_keeps_warnings_non_blocking()
    {
        var path = Path.Combine(RepositoryRoot, "src", "Directory.Build.props");
        Assert.True(File.Exists(path), $"Expected {path} to scope the complexity findings to production code.");

        var content = File.ReadAllText(path);
        Assert.Contains("../Directory.Build.props", content);
        Assert.Contains("CodeMetricsConfig.txt", content);
        Assert.Contains("CA1502", content);
        Assert.Contains("CA1505", content);
        Assert.Contains("WarningsNotAsErrors", content);
    }

    [Fact]
    public void Root_Directory_Build_Props_still_treats_warnings_as_errors_by_default()
    {
        var path = Path.Combine(RepositoryRoot, "Directory.Build.props");
        var content = File.ReadAllText(path);
        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", content);
    }
}
