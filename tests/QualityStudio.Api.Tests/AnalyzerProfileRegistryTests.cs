using System.Text.Json;
using Microsoft.Extensions.Options;
using Xunit;

namespace QualityStudio.Api.Tests;

public sealed class AnalyzerProfileRegistryTests
{
    [Fact]
    public void Host_profile_resolves_to_structured_process_arguments()
    {
        var options = new RepositoryOptions
        {
            Security = new ApiSecurityOptions { CommandBackedAnalyzersEnabled = true },
            AnalyzerProfiles =
            [
                new AnalyzerProfileOptions
                {
                    Id = "typescript-strict",
                    SensorId = "tsc",
                    Executable = "npx",
                    Arguments = ["--no-install", "tsc", "--noEmit", "--pretty", "false", "{target}"],
                    ReportPath = ".quality/analyzers/tsc.txt",
                    WorkingDirectory = "frontend",
                    ProducerVersion = "5.9.2",
                },
            ],
        };
        var profiles = new AnalyzerProfileRegistry(Options.Create(options));

        var resolved = profiles.Resolve(new RepositorySensorConfiguration(
            "tsc", Enabled: true, ProfileId: "typescript-strict"))!;

        Assert.Equal("npx", resolved["profileExecutable"]);
        Assert.Equal(".quality/analyzers/tsc.txt", resolved["reportPath"]);
        Assert.Equal("frontend", resolved["workingDirectory"]);
        Assert.Equal("5.9.2", resolved["producerVersion"]);
        Assert.Equal("{target}", JsonSerializer.Deserialize<string[]>(resolved["profileArguments"])!.Last());
        Assert.False(resolved.ContainsKey("command"));
    }

    [Fact]
    public void Repository_command_keys_are_rejected_case_insensitively()
    {
        Assert.True(AnalyzerProfileRegistry.ContainsForbiddenRepositoryConfiguration(
            new Dictionary<string, string> { ["Command"] = "/bin/sh -c env" }));
        Assert.True(AnalyzerProfileRegistry.ContainsForbiddenRepositoryConfiguration(
            new Dictionary<string, string> { ["PROFILEARGUMENTS"] = "[]" }));
    }

    [Fact]
    public void Host_profile_paths_cannot_traverse_out_of_the_repository()
    {
        var options = new RepositoryOptions
        {
            AnalyzerProfiles =
            [
                new AnalyzerProfileOptions
                {
                    Id = "escaped",
                    SensorId = "sarif",
                    Executable = "analyzer",
                    ReportPath = "../host-output.sarif",
                },
            ],
        };

        Assert.Throws<InvalidOperationException>(() => new AnalyzerProfileRegistry(Options.Create(options)));
    }
}
