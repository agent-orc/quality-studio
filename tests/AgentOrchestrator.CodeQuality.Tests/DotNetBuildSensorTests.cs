namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class DotNetBuildSensorTests
{
    [Fact]
    public void Parse_distinguishes_compiler_errors_from_analyzer_warnings()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-dotnet-parser"));
        var output = $"""
            {Path.Combine(root, "src", "Thing.cs")}(12,7): warning CA1822: Mark members as static [Thing.csproj]
            {Path.Combine(root, "src", "Broken.cs")}(4,2): error CS1002: ; expected [Thing.csproj]
            """;

        var findings = DotNetBuildSensor.Parse(output, root);

        Assert.Equal(2, findings.Count);
        var compiler = Assert.Single(findings, finding => finding.RuleId == "CS1002");
        Assert.Equal("compiler", compiler.Aspect);
        Assert.Equal(FindingSeverity.High, compiler.Severity);
        Assert.Equal("dotnet-build", compiler.Source!.SensorId);
        var analyzer = Assert.Single(findings, finding => finding.RuleId == "CA1822");
        Assert.Equal("analyzer", analyzer.Aspect);
        Assert.Equal(FindingSeverity.Medium, analyzer.Severity);
        Assert.Equal("src/Thing.cs", Assert.Single(analyzer.Locations).Path);

        var repeated = DotNetBuildSensor.Parse(output, root);
        Assert.Equal(findings.Select(finding => finding.Fingerprint),
            repeated.Select(finding => finding.Fingerprint));
    }

    [Fact]
    public async Task Run_restores_then_builds_and_returns_deterministic_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-dotnet-build-").FullName;
        try
        {
            var project = Path.Combine(root, "Sample.csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.Current.CancellationToken);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "10.0.301", string.Empty),
                new SensorCommandResult(0, "restored", string.Empty),
                new SensorCommandResult(1,
                    $"{Path.Combine(root, "Sample.cs")}(2,3): error CS1002: ; expected [Sample.csproj]",
                    string.Empty));

            var result = await new DotNetBuildSensor(runner).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal("CS1002", Assert.Single(result.Findings).RuleId);
            Assert.Equal("10.0.301", result.Provenance.ToolVersions["dotnet"]);
            Assert.Equal(["--version"], runner.Calls[0].Arguments);
            Assert.Equal("restore", runner.Calls[1].Arguments[0]);
            Assert.Equal("build", runner.Calls[2].Arguments[0]);
            Assert.Contains("--no-restore", runner.Calls[2].Arguments);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_build_without_parseable_diagnostics_is_unavailable_not_clean()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-dotnet-build-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "10.0.301", string.Empty),
                new SensorCommandResult(0, "restored", string.Empty),
                new SensorCommandResult(1, "Build failed.", string.Empty));

            var result = await new DotNetBuildSensor(runner).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Empty(result.Findings);
            Assert.Contains("without a parseable error", result.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class QueueRunner(params SensorCommandResult[] results) : ISensorCommandRunner
    {
        private readonly Queue<SensorCommandResult> results = new(results);
        private readonly List<Call> calls = [];

        public IReadOnlyList<Call> Calls => calls;

        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            calls.Add(new Call(executable, arguments.ToArray()));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(string Executable, IReadOnlyList<string> Arguments);
}
