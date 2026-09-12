using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AnalyzerProfileTests
{
    private static readonly AnalyzerProfileCatalog Catalog = new(
    [
        new AnalyzerProfile("lint", "eslint", "node lint.js --out {reportPath}", ".", "reports/lint.sarif"),
    ]);

    [Fact]
    public void An_inline_command_is_refused_while_commands_are_host_owned()
    {
        var resolved = AnalyzerInvocation.TryResolve("eslint",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["command"] = "sh -c compromised" },
            Catalog, out _, out var refusal);

        Assert.False(resolved);
        Assert.Contains("host-owned", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_that_opts_back_in_still_accepts_an_inline_command()
    {
        var permissive = new AnalyzerProfileCatalog(Catalog.ForSensor("eslint"), allowInlineCommands: true);

        var resolved = AnalyzerInvocation.TryResolve("eslint",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command"] = "node other.js",
                ["reportPath"] = "reports/other.sarif",
            },
            permissive, out var invocation, out _);

        Assert.True(resolved);
        Assert.Equal("node other.js", invocation.Command);
    }

    [Fact]
    public void An_unknown_profile_names_the_profiles_the_host_offers()
    {
        var resolved = AnalyzerInvocation.TryResolve("eslint",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "nope" },
            Catalog, out _, out var refusal);

        Assert.False(resolved);
        Assert.Contains("lint", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_supplies_the_command_and_the_repository_may_still_redirect_its_paths()
    {
        var fromProfile = AnalyzerInvocation.TryResolve("eslint",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "lint" },
            Catalog, out var plain, out _);
        var redirected = AnalyzerInvocation.TryResolve("eslint",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile"] = "lint",
                ["reportPath"] = "elsewhere/lint.sarif",
                ["workingDirectory"] = "frontend",
            },
            Catalog, out var moved, out _);

        Assert.True(fromProfile);
        Assert.Equal("node lint.js --out {reportPath}", plain.Command);
        Assert.Equal("reports/lint.sarif", plain.ReportPath);
        Assert.Equal(".", plain.WorkingDirectory);
        Assert.True(redirected);
        Assert.Equal("node lint.js --out {reportPath}", moved.Command);
        Assert.Equal("elsewhere/lint.sarif", moved.ReportPath);
        Assert.Equal("frontend", moved.WorkingDirectory);
    }

    [Fact]
    public void A_profile_of_another_sensor_is_not_reachable()
    {
        Assert.False(Catalog.TryResolve("tsc", "lint", out _));
        Assert.True(Catalog.TryResolve("eslint", "lint", out _));
    }

    [Fact]
    public void The_built_in_catalogue_covers_the_command_backed_analyzers_and_refuses_inline_commands()
    {
        Assert.False(AnalyzerProfileCatalog.BuiltIn.AllowInlineCommands);
        Assert.NotEmpty(AnalyzerProfileCatalog.BuiltIn.ForSensor("eslint"));
        Assert.NotEmpty(AnalyzerProfileCatalog.BuiltIn.ForSensor("roslyn"));
        Assert.NotEmpty(AnalyzerProfileCatalog.BuiltIn.ForSensor("tsc"));
    }

    [Fact]
    public void A_host_file_replaces_the_built_in_profiles()
    {
        var directory = Directory.CreateTempSubdirectory("quality-studio-profiles-").FullName;
        var path = Path.Combine(directory, AnalyzerProfileCatalog.DefaultFileName);
        try
        {
            File.WriteAllText(path, """
                {
                  "profiles": [
                    { "id": "house-lint", "sensor": "eslint", "command": "node house.js --out {reportPath}" }
                  ]
                }
                """);

            var catalog = AnalyzerProfileCatalog.Load(path);

            Assert.Equal(["house-lint"], catalog.ForSensor("eslint").Select(profile => profile.Id));
            Assert.Empty(catalog.ForSensor("tsc"));
        }
        finally
        {
            TemporaryDirectory.Delete(directory);
        }
    }

    [Fact]
    public void A_malformed_profile_file_fails_the_host_instead_of_a_scan()
    {
        var directory = Directory.CreateTempSubdirectory("quality-studio-profiles-").FullName;
        var path = Path.Combine(directory, AnalyzerProfileCatalog.DefaultFileName);
        try
        {
            File.WriteAllText(path, "{ \"profiles\": [] }");
            Assert.Throws<InvalidOperationException>(() => AnalyzerProfileCatalog.Load(path));

            File.WriteAllText(path, "{ \"profiles\": [ { \"id\": \"x\", \"sensor\": \"eslint\", \"command\": \"\" } ] }");
            Assert.Throws<ArgumentException>(() => AnalyzerProfileCatalog.Load(path));
        }
        finally
        {
            TemporaryDirectory.Delete(directory);
        }
    }

    [Fact]
    public void The_configuration_allowlist_never_contains_the_command_key()
    {
        foreach (var sensor in new[] { "sarif", "roslyn", "eslint", "tsc", "coverage", "dependencies", "dotnet-build", "gitleaks" })
        {
            var allowed = AnalyzerSensorConfiguration.AllowedKeys(sensor);
            Assert.NotNull(allowed);
            Assert.DoesNotContain(AnalyzerSensorConfiguration.CommandKey, allowed, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Null(AnalyzerSensorConfiguration.AllowedKeys("a-sensor-this-table-does-not-know"));
    }
}
