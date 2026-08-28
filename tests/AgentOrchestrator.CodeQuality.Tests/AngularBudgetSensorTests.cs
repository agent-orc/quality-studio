namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AngularBudgetSensorTests
{
    private const string SampleOutput = """
        ▲ [WARNING] bundle initial exceeded maximum budget. Budget 350.00 kB was not met by 128.30 kB with a total of 478.30 kB.

        ▲ [WARNING] src/app/editor/editor.css exceeded maximum budget. Budget 10.00 kB was not met by 1.06 kB with a total of 11.06 kB.
        """;

    [Fact]
    public void Parse_distinguishes_bundle_budget_from_component_style_budget()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-ng-budget-parser"));
        var working = Path.Combine(root, "frontend");

        var findings = AngularBudgetSensor.Parse(SampleOutput, root, working);

        Assert.Equal(2, findings.Count);
        var bundle = Assert.Single(findings, finding => finding.RuleId == "ng-budget" &&
            finding.Locations[0].Path == "frontend/angular.json");
        Assert.Equal(FindingSeverity.Medium, bundle.Severity);
        Assert.Equal("performance", bundle.Aspect);
        Assert.Contains("Initial bundle", bundle.Title, StringComparison.Ordinal);

        var style = Assert.Single(findings, finding =>
            finding.Locations[0].Path == "frontend/src/app/editor/editor.css");
        Assert.Equal(FindingSeverity.Medium, style.Severity);
        Assert.Contains("editor.css", style.Title, StringComparison.Ordinal);

        var repeated = AngularBudgetSensor.Parse(SampleOutput, root, working);
        Assert.Equal(findings.Select(finding => finding.Fingerprint),
            repeated.Select(finding => finding.Fingerprint));
    }

    [Fact]
    public void Parse_maps_error_marker_to_high_severity()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-ng-budget-parser"));
        var working = Path.Combine(root, "frontend");
        const string output =
            "✘ [ERROR] bundle initial exceeded maximum budget. Budget 400.00 kB was not met by 78.30 kB " +
            "with a total of 478.30 kB.";

        var finding = Assert.Single(AngularBudgetSensor.Parse(output, root, working));

        Assert.Equal(FindingSeverity.High, finding.Severity);
    }

    [Fact]
    public void Parse_ignores_output_with_no_budget_lines()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-ng-budget-parser"));
        var working = Path.Combine(root, "frontend");

        var findings = AngularBudgetSensor.Parse(
            "Application bundle generation complete. [6.154 seconds]", root, working);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task Run_returns_deterministic_findings_from_configured_command()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-ng-budget-run-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frontend"));
            var runner = new QueueRunner(new SensorCommandResult(0, SampleOutput, string.Empty));

            var result = await new AngularBudgetSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workingDirectory"] = "frontend",
                    ["reportPath"] = ".quality/preflight/ng-budget.log",
                    ["command"] = "node node_modules/@angular/cli/bin/ng.js build --configuration production",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal(2, result.Findings.Count);
            Assert.True(File.Exists(Path.Combine(root, ".quality/preflight/ng-budget.log")));
            Assert.Equal("frontend", Path.GetRelativePath(root, runner.Calls[0].WorkingDirectory));
            Assert.Equal("node", runner.Calls[0].Executable);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_build_without_parseable_budget_diagnostics_is_unavailable_not_clean()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-ng-budget-run-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frontend"));
            var runner = new QueueRunner(new SensorCommandResult(1, "Some unrelated build failure.", string.Empty));

            var result = await new AngularBudgetSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workingDirectory"] = "frontend",
                    ["reportPath"] = ".quality/preflight/ng-budget.log",
                    ["command"] = "node node_modules/@angular/cli/bin/ng.js build --configuration production",
                }),
                TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Empty(result.Findings);
            Assert.Contains("without a parseable budget diagnostic", result.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Clean_build_within_budget_reports_available_with_no_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-ng-budget-run-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "frontend"));
            var runner = new QueueRunner(
                new SensorCommandResult(0, "Application bundle generation complete. [6.154 seconds]", string.Empty));

            var result = await new AngularBudgetSensor(runner).RunAsync(
                new SensorScanRequest(root, Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["workingDirectory"] = "frontend",
                    ["reportPath"] = ".quality/preflight/ng-budget.log",
                    ["command"] = "node node_modules/@angular/cli/bin/ng.js build --configuration production",
                }),
                TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Empty(result.Findings);
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
            calls.Add(new Call(executable, arguments.ToArray(), workingDirectory));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);
}
