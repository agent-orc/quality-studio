using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class SarifSensorTests
{
    [Fact]
    public async Task ParseAsync_MapsRoslynRulesLocationsAndProducerProvenance()
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
            var location = Assert.Single(finding.Locations);
            Assert.Equal("src/Calculator.cs", location.Path);
            Assert.Equal(5, location.Range!.Start.Line);
            Assert.Equal("Sample.Calculator.Calculate", location.SymbolId);
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
            Assert.Equal("Microsoft.CodeAnalysis", finding.Source.Producer);
            Assert.Equal("4.14.0", finding.Source.ProducerVersion);
            using var evidence = JsonDocument.Parse(finding.Evidence!);
            Assert.Equal("MarkMembersAsStatic",
                evidence.RootElement.GetProperty("rule").GetProperty("name").GetString());
            Assert.Equal("Performance",
                evidence.RootElement.GetProperty("rule").GetProperty("properties").GetProperty("category").GetString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ParseAsync_MapsEslintNestedLocationsAndMessageMetadata()
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
                location => location.Path == "frontend/src/service.ts" && location.Range!.Start.Line == 8);
            Assert.Equal("ESLint", finding.Source!.Producer);
            Assert.Equal(0, finding.Source.RunIndex);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RunAsync_ReturnsExplicitUnavailableWhenCommandIsNotInstalled()
    {
        var root = CreateRepository("src/a.ts");
        try
        {
            var result = await new SarifSensor(new MissingCommandRunner()).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["command"] = "missing-analyzer --sarif {reportPath}",
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
    public async Task RunAsync_ReportsKnownWarningExactlyOnceWithAnalyzerSource()
    {
        var root = CreateRepository("src/Calculator.cs");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".quality", "analyzers"));
            File.Copy(Fixture("roslyn.sarif.json"), Path.Combine(root, ".quality", "analyzers", "roslyn.sarif"));

            var result = await new SarifSensor().RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>
                {
                    ["reportPath"] = ".quality/analyzers/roslyn.sarif",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("CA1822", finding.RuleId);
            Assert.Equal("sarif", finding.Source!.SensorId);
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TypeScriptParser_MapsNoEmitTypeErrorExactlyOnce()
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
            Assert.Equal(7, finding.Locations[0].Range!.Start.Line);
            Assert.Equal("tsc", finding.Source!.SensorId);
            Assert.Equal("TypeScript", finding.Source.Producer);
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source.Kind);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TypeScriptRun_CapturesNoEmitReportAndTreatsDiagnosticsAsEvidence()
    {
        var root = CreateRepository("frontend/src/app.ts");
        try
        {
            var sensor = new TypeScriptAnalyzerSensor(new RecordedTscRunner(
                "src/app.ts(7,11): error TS2322: Type 'string' is not assignable to type 'number'.\n"));

            var result = await sensor.RunAsync(new SensorScanRequest(
                root,
                Configuration: new Dictionary<string, string>
                {
                    ["command"] = "tsc --noEmit --pretty false",
                    ["reportPath"] = ".quality/analyzers/tsc.txt",
                    ["workingDirectory"] = "frontend",
                    ["producerVersion"] = "5.9.2",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal("TS2322", Assert.Single(result.Findings).RuleId);
            Assert.Equal("5.9.2", result.Provenance.ToolVersions["typescript"]);
            Assert.True(File.Exists(Path.Combine(root, ".quality", "analyzers", "tsc.txt")));
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

    private sealed class RecordedTscRunner(string output) : ISensorCommandRunner
    {
        public Task<SensorCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SensorCommandResult(2, output, string.Empty));
    }
}
