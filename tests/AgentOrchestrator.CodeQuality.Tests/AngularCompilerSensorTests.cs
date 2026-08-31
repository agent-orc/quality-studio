using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AngularCompilerSensorTests
{
    [Fact]
    public void Parse_normalizes_real_ng8102_location_and_stable_fingerprint()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-parser"));
        var output = """
            [96msrc/app/editor/editor.html[0m:[93m169[0m:[93m61[0m - [93mwarning[0m[90m NG8102: [0mThe left side of this nullish coalescing operation does not include 'null' or 'undefined' in its type, therefore the '??' operator can be safely removed.

            169                   <input [value]="drafts()[row.thread.id] ?? ''">
                                                                 ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            """;

        var findings = AngularCompilerSensor.Parse(
            output, root, Path.Combine(root, "frontend"), "20.3.29");

        var finding = Assert.Single(findings);
        Assert.Equal("NG8102", finding.RuleId);
        Assert.Equal("compiler", finding.Aspect);
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("frontend/src/app/editor/editor.html", Assert.Single(finding.Locations).Path);
        Assert.Equal(new FindingPosition(169, 61), finding.Locations[0].Range!.Start);
        Assert.Equal(89, finding.Locations[0].Range!.End.Column);
        Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
        Assert.Equal("angular-compiler", finding.Source.SensorId);
        Assert.Equal("AngularCompiler", finding.Source.Producer);
        Assert.Equal("20.3.29", finding.Source.ProducerVersion);

        var repeated = Assert.Single(AngularCompilerSensor.Parse(
            output, root, Path.Combine(root, "frontend"), "20.3.29"));
        Assert.Equal(finding.Fingerprint, repeated.Fingerprint);
    }

    [Fact]
    public void Parse_deduplicates_and_orders_ng_and_ts_diagnostics()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-order"));
        var output = """
            src/app/z.ts:8:4 - error TS2322: Type 'string' is not assignable to type 'number'.
            src/app/a.html:3:2 - warning NG8102: The fallback is unreachable.
            src/app/z.ts:8:4 - error TS2322: Type 'string' is not assignable to type 'number'.
            """;

        var findings = AngularCompilerSensor.Parse(output, root, Path.Combine(root, "frontend"));

        Assert.Equal(2, findings.Count);
        Assert.Equal("frontend/src/app/a.html", findings[0].Locations[0].Path);
        Assert.Equal("NG8102", findings[0].RuleId);
        Assert.Equal("TS2322", findings[1].RuleId);
        Assert.Equal(FindingSeverity.High, findings[1].Severity);
    }

    [Fact]
    public async Task Availability_requires_project_and_repository_local_compiler()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-availability-").FullName;
        try
        {
            await CreateAngularTargetAsync(root);
            var runner = new QueueRunner(new SensorCommandResult(0, "0.0.0", string.Empty));

            var available = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);

            Assert.True(available.Available);
            Assert.Null(available.UnavailableReason);
            Assert.Equal("20.3.29", available.ToolVersions!["angularCompiler"]);
            var call = Assert.Single(runner.Calls);
            Assert.Equal("node", call.Executable);
            Assert.EndsWith("frontend/node_modules/@angular/compiler-cli/bundles/src/bin/ngc.js",
                call.Arguments[0].Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Equal("--version", call.Arguments[1]);

            File.Delete(Path.Combine(root, "frontend", "tsconfig.app.json"));
            var missingProject = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);
            Assert.False(missingProject.Available);
            Assert.Contains("tsconfig.app.json", missingProject.UnavailableReason, StringComparison.Ordinal);
            Assert.True(missingProject.UnavailableReason!.Length <= 500);

            await File.WriteAllTextAsync(Path.Combine(root, "frontend", "tsconfig.app.json"), "{}",
                TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(root, "frontend", "node_modules", "@angular", "compiler-cli",
                "bundles", "src", "bin", "ngc.js"));
            var missingCompiler = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);
            Assert.False(missingCompiler.Available);
            Assert.Contains("repository-local ngc", missingCompiler.UnavailableReason,
                StringComparison.Ordinal);
            Assert.True(missingCompiler.UnavailableReason!.Length <= 500);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Run_executes_turnkey_ngc_command_and_nonparseable_failure_is_unavailable()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-run-").FullName;
        try
        {
            await CreateAngularTargetAsync(root);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(1, "Compilation failed.", string.Empty));

            var result = await new AngularCompilerSensor(runner, root).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Empty(result.Findings);
            Assert.Contains("without parseable diagnostics", result.UnavailableReason,
                StringComparison.Ordinal);
            Assert.Equal("node", runner.Calls[1].Executable);
            Assert.Equal("-p", runner.Calls[1].Arguments[1]);
            Assert.Equal("frontend/tsconfig.app.json", runner.Calls[1].Arguments[2]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Prompt_projection_is_compact_and_full_diagnostic_remains_standalone()
    {
        var finding = new ReviewFinding(
            "angular-ng8102-aaaaaaaaaaaa",
            "compiler",
            FindingSeverity.Medium,
            "NG8102: Nullish fallback is unreachable",
            new string('d', 4_000),
            new string('r', 4_000),
            [new FindingLocation(
                "frontend/src/app/editor/editor.html",
                new FindingRange(new FindingPosition(169, 61), new FindingPosition(169, 89)))],
            "sha256:" + new string('a', 64),
            "NG8102",
            Source: new FindingSource(
                FindingSourceKind.Deterministic,
                "angular-compiler",
                "AngularCompiler",
                "20.3.29"));
        var evidence = new SensorScanResult(
            true,
            null,
            [finding],
            new SensorProvenance(
                "angular-compiler", "1.0.0", "repository", ".", "2026-08-31T10:00:00Z",
                new Dictionary<string, string> { ["angularCompiler"] = "20.3.29" }));

        var promptJson = DeterministicEvidenceProjection.ToPromptJson([evidence]);

        Assert.True(promptJson.Length <= DeterministicEvidenceProjection.MaximumPromptCharacters);
        Assert.DoesNotContain(new string('d', 100), promptJson, StringComparison.Ordinal);
        using var prompt = JsonDocument.Parse(promptJson);
        var projected = Assert.Single(prompt.RootElement.EnumerateArray());
        Assert.Equal("angular-compiler", projected.GetProperty("sensorId").GetString());
        Assert.StartsWith("sha256:", projected.GetProperty("resultHash").GetString(),
            StringComparison.Ordinal);
        var projectedFinding = Assert.Single(projected.GetProperty("findings").EnumerateArray());
        Assert.Equal("NG8102", projectedFinding.GetProperty("ruleId").GetString());
        Assert.Equal("medium", projectedFinding.GetProperty("severity").GetString());
        Assert.Equal("frontend/src/app/editor/editor.html",
            projectedFinding.GetProperty("path").GetString());
        Assert.Equal(169, projectedFinding.GetProperty("range").GetProperty("start")
            .GetProperty("line").GetInt32());
        Assert.Equal(new string('d', 4_000), finding.Description);
    }

    private static async Task CreateAngularTargetAsync(string root)
    {
        var frontend = Path.Combine(root, "frontend");
        var compiler = Path.Combine(frontend, "node_modules", "@angular", "compiler-cli");
        Directory.CreateDirectory(Path.Combine(compiler, "bundles", "src", "bin"));
        await File.WriteAllTextAsync(Path.Combine(frontend, "tsconfig.app.json"), "{}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(compiler, "package.json"),
            "{\"version\":\"20.3.29\"}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(compiler, "bundles", "src", "bin", "ngc.js"),
            "#!/usr/bin/env node", TestContext.Current.CancellationToken);
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
