namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class BuildPreflightSensorTests
{
    [Fact]
    public void DotNet_build_parser_distinguishes_blocking_errors_from_warnings()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-dotnet-parser"));
        var output = $"""
            {Path.Combine(root, "src", "Thing.cs")}(12,7): warning CA1502: Avoid excessive complexity [Thing.csproj]
            {Path.Combine(root, "src", "Broken.cs")}(4,2): error CS1002: ; expected [Thing.csproj]
            """;

        var findings = DotNetBuildSensor.Parse(output, root);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.RuleId == "CS1002" && finding.Severity == FindingSeverity.High);
        Assert.Contains(findings, finding => finding.RuleId == "CA1502" && finding.Severity == FindingSeverity.Medium);
        Assert.True(new DotNetBuildSensor(new QueueRunner()).HasBlockingFindings(new SensorScanResult(
            true, null, findings, Provenance("dotnet-build"))));
        var repeated = DotNetBuildSensor.Parse(output, root);
        Assert.Equal(findings.Select(finding => finding.Fingerprint), repeated.Select(finding => finding.Fingerprint));
        Assert.Equal(12, findings.Single(finding => finding.RuleId == "CA1502").Locations.Single().Range!.Start.Line);
    }

    [Fact]
    public void Angular_compiler_parser_normalizes_template_warning_and_error_locations()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-parser"));
        var output = """
            [96mfrontend/src/app/editor/editor.html[0m:[93m171[0m:[93m61[0m - [93mwarning[0m[90m NG8102: [0mThe fallback is unreachable.
            frontend/src/app/editor/broken.html:2:4 - error NG5002: Parser Error
            """;

        var findings = AngularCompilerSensor.ParseAngular(output, root, "20.3.26");

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.RuleId == "NG8102" &&
            Assert.Single(finding.Locations).Path == "frontend/src/app/editor/editor.html");
        Assert.Contains(findings, finding => finding.RuleId == "NG5002" && finding.Severity == FindingSeverity.High);
    }

    [Fact]
    public void Angular_budget_parser_blocks_only_error_budget_findings()
    {
        var output = """
            ▲ [WARNING] bundle initial exceeded maximum budget. Budget 350.00 kB was not met by 126.18 kB with a total of 476.18 kB.
            ✘ [ERROR] bundle initial exceeded maximum budget. Budget 480.00 kB was not met by 14.94 kB with a total of 494.94 kB.
            ▲ [WARNING] Module 'legacy' used by 'src/app/editor.ts' is not ESM
            """;
        var sensor = new AngularBudgetSensor(new QueueRunner());

        var findings = AngularBudgetSensor.Parse(output, ".", "frontend/angular.json", "20.3.32");

        Assert.Equal(3, findings.Count);
        Assert.True(sensor.HasBlockingFindings(new SensorScanResult(true, null, findings, Provenance(sensor.Id))));
        Assert.Equal(2, findings.Count(finding => finding.RuleId == "angular-budget"));
        Assert.Contains(findings, finding => finding.RuleId == "angular-build");
    }

    [Fact]
    public async Task Npm_ci_uses_lock_hash_marker_and_skips_an_unchanged_second_install()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-npm-ci-").FullName;
        try
        {
            var frontend = Directory.CreateDirectory(Path.Combine(root, "frontend")).FullName;
            await File.WriteAllTextAsync(Path.Combine(frontend, "package-lock.json"), "{\"lockfileVersion\":3}",
                TestContext.Current.CancellationToken);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "11.4.2", ""),
                new SensorCommandResult(0, "installed", ""),
                new SensorCommandResult(0, "11.4.2", ""));
            var sensor = new NpmCiSensor(runner);

            var first = await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);
            var second = await sensor.RunAsync(new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.True(first.Available);
            Assert.True(second.Available);
            Assert.Single(runner.Calls, call => call.Arguments.SequenceEqual(["ci"]));
            Assert.True(File.Exists(Path.Combine(frontend, "node_modules", ".quality-studio-lock-hash")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Dependency_sensor_batches_a_solution_into_one_dotnet_audit()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-dependency-batch-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Quality.slnx"), "<Solution />",
                TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "src", "One"));
            Directory.CreateDirectory(Path.Combine(root, "src", "Two"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "One", "One.csproj"), "<Project />",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "Two", "Two.csproj"), "<Project />",
                TestContext.Current.CancellationToken);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "10.0.100", ""),
                new SensorCommandResult(0, "{\"projects\":[]}", ""));

            var result = await new DependencyVulnerabilitySensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["ecosystems"] = "dotnet",
                }), TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            var audit = Assert.Single(runner.Calls, call => call.Arguments.Contains("--vulnerable"));
            Assert.EndsWith("Quality.slnx", audit.Arguments[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static SensorProvenance Provenance(string id) =>
        new(id, "1.0.0", "repository", ".", DateTimeOffset.UtcNow.ToString("O"),
            new Dictionary<string, string>());

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
            calls.Add(new Call(executable, arguments.ToArray(), workingDirectory));
            if (results.Count == 0) return Task.FromResult(new SensorCommandResult(0, string.Empty, string.Empty));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);
}
