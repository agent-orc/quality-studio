using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class SarifSensorTests
{
    [Fact]
    public async Task RoslynFixture_MapsRuleMetadataAndDeduplicatesKnownWarning()
    {
        var root = CreateRepository("src/Calculator.cs");
        try
        {
            await using var fixture = File.OpenRead(Fixture("roslyn.sarif.json"));

            var finding = Assert.Single(await SarifSensor.ParseAsync(
                fixture, root, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("CA1822", finding.RuleId);
            Assert.Equal(FindingSeverity.Medium, finding.Severity);
            Assert.Equal("Mark members as static", finding.Title);
            Assert.Equal("Sample.Calculator.Calculate", Assert.Single(finding.Locations).SymbolId);
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
            Assert.Equal("Microsoft.CodeAnalysis", finding.Source.Producer);
            Assert.Equal("4.14.0", finding.Source.ProducerVersion);
            using var evidence = JsonDocument.Parse(finding.Evidence!);
            Assert.Equal("Performance",
                evidence.RootElement.GetProperty("rule").GetProperty("properties")
                    .GetProperty("category").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EslintFixture_MapsNestedLocationsAndRuleMessage()
    {
        var root = CreateRepository("frontend/src/app.ts", "frontend/src/service.ts");
        try
        {
            await using var fixture = File.OpenRead(Fixture("eslint.sarif.json"));

            var finding = Assert.Single(await SarifSensor.ParseAsync(
                fixture, root, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal("@typescript-eslint/no-floating-promises", finding.RuleId);
            Assert.Equal(FindingSeverity.High, finding.Severity);
            Assert.Equal("Promises must be awaited: loadData().", finding.Description);
            Assert.Equal(3, finding.Locations.Count);
            Assert.Contains(finding.Locations,
                location => location.Path == "frontend/src/service.ts" &&
                            location.Range!.Start.Line == 12);
            Assert.Equal("ESLint", finding.Source!.Producer);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MissingAnalyzer_IsAnExplicitUnavailableResult()
    {
        var root = CreateRepository("src/a.ts");
        try
        {
            var result = await new SarifSensor(new MissingCommandRunner()).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["profileExecutable"] = "missing-analyzer",
                    ["profileArguments"] = JsonSerializer.Serialize(new[] { "--sarif", "{reportPath}" }),
                    ["reportPath"] = ".quality/analyzers/missing.sarif",
                }),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("unavailable", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.Findings);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FreeFormShellCommand_IsRejectedWithoutExecution()
    {
        var root = CreateRepository("src/a.ts");
        try
        {
            var result = await new SarifSensor(new UnexpectedCommandRunner()).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["command"] = OperatingSystem.IsWindows()
                        ? "powershell.exe -NoProfile -Command Get-ChildItem Env:"
                        : "/bin/sh -c env",
                    ["reportPath"] = ".quality/analyzers/result.sarif",
                }), TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Contains("host-owned analyzer profile", result.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RoslynSensor_ReportsKnownWarningExactlyOnceWithAnalyzerSource()
    {
        var root = CreateRepository("src/Calculator.cs");
        try
        {
            var report = Path.Combine(root, ".quality", "analyzers", "roslyn.sarif");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.Copy(Fixture("roslyn.sarif.json"), report);

            var result = await new RoslynAnalyzerSensor().RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["reportPath"] = ".quality/analyzers/roslyn.sarif",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("CA1822", finding.RuleId);
            Assert.Equal("roslyn", finding.Source!.SensorId);
            Assert.Equal("Microsoft.CodeAnalysis", finding.Source.Producer);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TypeScriptOutput_MapsOneDiagnosticWithRuleAndSource()
    {
        var root = CreateRepository("frontend/src/app.ts");
        try
        {
            var output = """
                frontend/src/app.ts(7,11): error TS2322: Type 'string' is not assignable to type 'number'.
                frontend/src/app.ts(7,11): error TS2322: Type 'string' is not assignable to type 'number'.
                """;

            var finding = Assert.Single(TypeScriptAnalyzerSensor.Parse(output, root));

            Assert.Equal("TS2322", finding.RuleId);
            Assert.Equal("frontend/src/app.ts", Assert.Single(finding.Locations).Path);
            Assert.Equal("tsc", finding.Source!.SensorId);
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TypeScriptSensor_CapturesConfiguredNoEmitReport()
    {
        var root = CreateRepository("frontend/src/app.ts");
        try
        {
            var sensor = new TypeScriptAnalyzerSensor(new RecordedRunner(
                2,
                "src/app.ts(7,11): error TS2322: Type 'string' is not assignable to type 'number'.\n"));

            var result = await sensor.RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["profileExecutable"] = "npx",
                    ["profileArguments"] = JsonSerializer.Serialize(
                        new[] { "--no-install", "tsc", "--noEmit", "--pretty", "false" }),
                    ["reportPath"] = ".quality/analyzers/tsc.txt",
                    ["workingDirectory"] = "frontend",
                    ["producerVersion"] = "5.9.2",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal("TS2322", Assert.Single(result.Findings).RuleId);
            Assert.True(File.Exists(Path.Combine(root, ".quality", "analyzers", "tsc.txt")));
            Assert.Equal("5.9.2", result.Provenance.ToolVersions["typescript"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sarif", name);

    private static string CreateRepository(params string[] paths)
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-sarif-").FullName;
        foreach (var path in paths)
        {
            var absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, string.Empty);
        }
        return root;
    }

    private sealed class MissingCommandRunner : ISensorCommandRunner
    {
        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            throw new SecurityScannerUnavailableException("executable was not found");
    }

    private sealed class UnexpectedCommandRunner : ISensorCommandRunner
    {
        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("A rejected free-form command reached the process runner.");
    }

    private sealed class RecordedRunner(int exitCode, string output) : ISensorCommandRunner
    {
        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorCommandResult(exitCode, output, string.Empty));
    }
}
