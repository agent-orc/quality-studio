namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ComplexityThresholdConfigurationTests
{
    private static string RepositoryRoot => RepositoryTestContext.FindRepositoryRoot();

    [Fact]
    public void EditorConfig_enables_CA1502_and_CA1505_as_warnings_for_src_only()
    {
        var editorConfig = File.ReadAllText(Path.Combine(RepositoryRoot, ".editorconfig"));

        var srcSectionStart = editorConfig.IndexOf("[src/**.cs]", StringComparison.Ordinal);
        Assert.True(srcSectionStart >= 0, "Expected a [src/**.cs] section scoping complexity diagnostics to production code.");

        var srcSection = editorConfig[srcSectionStart..];
        Assert.Contains("dotnet_diagnostic.CA1502.severity = warning", srcSection, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.CA1505.severity = warning", srcSection, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeMetricsConfig_declares_explicit_thresholds()
    {
        var configPath = Path.Combine(RepositoryRoot, "CodeMetricsConfig.txt");
        Assert.True(File.Exists(configPath), "CodeMetricsConfig.txt must exist at the repository root.");

        var lines = File.ReadAllLines(configPath);
        Assert.Contains("CA1502: 25", lines);
        Assert.Contains("CA1505: 10", lines);
    }

    [Fact]
    public void DirectoryBuildProps_wires_thresholds_as_warnings_not_errors_and_excludes_tests()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains("<WarningsNotAsErrors>$(WarningsNotAsErrors);CA1502;CA1505</WarningsNotAsErrors>", props, StringComparison.Ordinal);
        Assert.Contains("<AdditionalFiles Include=\"$(MSBuildThisFileDirectory)CodeMetricsConfig.txt\"", props, StringComparison.Ordinal);
        Assert.Contains("Condition=\"'$(IsTestProject)' != 'true'\"", props, StringComparison.Ordinal);
        Assert.Contains("<NoWarn>$(NoWarn);CA1502;CA1505</NoWarn>", props, StringComparison.Ordinal);
    }
}
