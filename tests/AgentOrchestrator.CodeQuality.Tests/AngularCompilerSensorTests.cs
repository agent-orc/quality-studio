using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AngularCompilerSensorTests
{
    [Fact]
    public void Parse_normalizes_ng8102_at_the_reported_template_location_with_a_stable_fingerprint()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-parser"));
        var output = "\u001b[96msrc/app/editor/editor.html\u001b[0m:" +
                     "\u001b[93m171\u001b[0m:\u001b[93m61\u001b[0m - " +
                     "\u001b[93mwarning\u001b[0m\u001b[90m NG8102: \u001b[0m" +
                     "The left side of this nullish coalescing operation cannot be null.\n\n" +
                     "171 <input [value]=\"drafts()[row.thread.id] ?? ''\">\n" +
                     "                                                            ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n";

        var finding = Assert.Single(AngularCompilerSensor.Parse(
            output, root, Path.Combine(root, "frontend"), "20.3.29"));

        Assert.Equal("NG8102", finding.RuleId);
        Assert.Equal("compiler", finding.Aspect);
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("frontend/src/app/editor/editor.html", Assert.Single(finding.Locations).Path);
        Assert.Equal(new FindingPosition(171, 61), finding.Locations[0].Range!.Start);
        Assert.Equal(new FindingPosition(171, 89), finding.Locations[0].Range!.End);
        Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
        Assert.Equal("angular-compiler", finding.Source.SensorId);
        Assert.Equal("AngularCompiler", finding.Source.Producer);
        Assert.Equal("20.3.29", finding.Source.ProducerVersion);

        var repeated = Assert.Single(AngularCompilerSensor.Parse(
            output, root, Path.Combine(root, "frontend"), "20.3.29"));
        Assert.Equal(finding.Fingerprint, repeated.Fingerprint);
    }

    [Fact]
    public void Parse_surfaces_every_ng_and_ts_diagnostic_and_orders_them_deterministically()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-order"));
        const string output = """
            src/app/editor/editor.html:174:86 - warning NG8102: Second warning.
            src/app/other/other.ts(10,2): error TS2322: Type mismatch.
            src/app/editor/editor.html:169:61 - warning NG8102: First warning.
            src/app/editor/editor.html:169:61 - warning NG8102: First warning.
            """;

        var findings = AngularCompilerSensor.Parse(output, root, Path.Combine(root, "frontend"));
        var emittedDiagnostics = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(" NG", StringComparison.Ordinal) ||
                           line.Contains(" TS", StringComparison.Ordinal));

        Assert.Equal(emittedDiagnostics - 1, findings.Count);
        Assert.Equal([169, 174, 10], findings.Select(
            finding => finding.Locations[0].Range!.Start.Line));
        Assert.Equal(["NG8102", "NG8102", "TS2322"],
            findings.Select(finding => finding.RuleId));
        Assert.Equal(FindingSeverity.High, findings[2].Severity);
    }

    [Fact]
    public async Task Availability_requires_the_angular_project_and_repository_local_compiler()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var availableRoot = Directory.CreateTempSubdirectory("quality-studio-angular-available-").FullName;
        var missingCompilerRoot = Directory.CreateTempSubdirectory("quality-studio-angular-no-compiler-").FullName;
        var missingProjectRoot = Directory.CreateTempSubdirectory("quality-studio-angular-no-project-").FullName;
        try
        {
            await CreateAngularTargetAsync(availableRoot, includeCompiler: true, cancellationToken);
            await CreateAngularTargetAsync(missingCompilerRoot, includeCompiler: false, cancellationToken);
            var runner = new QueueRunner(new SensorCommandResult(0, "0.0.0", string.Empty));

            var available = await new AngularCompilerSensor(runner, availableRoot)
                .ProbeAvailabilityAsync(cancellationToken);
            var compilerMissing = await new AngularCompilerSensor(runner, missingCompilerRoot)
                .ProbeAvailabilityAsync(cancellationToken);
            var projectMissing = await new AngularCompilerSensor(runner, missingProjectRoot)
                .ProbeAvailabilityAsync(cancellationToken);

            Assert.True(available.Available);
            Assert.Equal("20.3.29", available.ToolVersions!["angularCompiler"]);
            Assert.Equal("5.9.3", available.ToolVersions["typescript"]);
            var call = Assert.Single(runner.Calls);
            Assert.Equal("node", call.Executable);
            Assert.EndsWith(
                "frontend/node_modules/@angular/compiler-cli/bundles/src/bin/ngc.js",
                call.Arguments[0].Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Equal("--version", call.Arguments[1]);
            Assert.False(compilerMissing.Available);
            Assert.Contains("repository-local ngc", compilerMissing.UnavailableReason,
                StringComparison.Ordinal);
            Assert.InRange(compilerMissing.UnavailableReason!.Length, 1, 1_000);
            Assert.False(projectMissing.Available);
            Assert.Contains("frontend/tsconfig.app.json", projectMissing.UnavailableReason,
                StringComparison.Ordinal);
            Assert.InRange(projectMissing.UnavailableReason!.Length, 1, 1_000);
        }
        finally
        {
            Directory.Delete(availableRoot, true);
            Directory.Delete(missingCompilerRoot, true);
            Directory.Delete(missingProjectRoot, true);
        }
    }

    [Fact]
    public async Task Failed_or_truncated_compiler_output_is_unavailable_not_clean()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var failedRoot = Directory.CreateTempSubdirectory("quality-studio-angular-failed-").FullName;
        var truncatedRoot = Directory.CreateTempSubdirectory("quality-studio-angular-truncated-").FullName;
        try
        {
            await CreateAngularTargetAsync(failedRoot, includeCompiler: true, cancellationToken);
            await CreateAngularTargetAsync(truncatedRoot, includeCompiler: true, cancellationToken);
            var failedRunner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(1, "Compilation failed.", string.Empty));
            var truncatedRunner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(1, "partial", string.Empty, OutputTruncated: true));

            var failed = await new AngularCompilerSensor(failedRunner).RunAsync(
                new SensorScanRequest(failedRoot), cancellationToken);
            var truncated = await new AngularCompilerSensor(truncatedRunner).RunAsync(
                new SensorScanRequest(truncatedRoot), cancellationToken);

            Assert.False(failed.Available);
            Assert.Empty(failed.Findings);
            Assert.Contains("without parseable diagnostics", failed.UnavailableReason,
                StringComparison.Ordinal);
            Assert.Equal("node", failedRunner.Calls[1].Executable);
            Assert.Equal("-p", failedRunner.Calls[1].Arguments[1]);
            Assert.Equal("frontend/tsconfig.app.json", failedRunner.Calls[1].Arguments[2]);
            Assert.False(truncated.Available);
            Assert.Contains("exceeded", truncated.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(failedRoot, true);
            Directory.Delete(truncatedRoot, true);
        }
    }

    [Fact]
    public async Task Nonzero_compiler_exit_with_parseable_diagnostics_returns_findings()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-diagnostics-").FullName;
        try
        {
            await CreateAngularTargetAsync(
                root, includeCompiler: true, TestContext.Current.CancellationToken);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(1,
                    "src/app/broken.ts:2:1 - error TS1185: Merge conflict marker encountered.",
                    string.Empty));

            var result = await new AngularCompilerSensor(runner).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            var finding = Assert.Single(result.Findings);
            Assert.Equal("TS1185", finding.RuleId);
            Assert.Equal(FindingSeverity.High, finding.Severity);
            Assert.Equal("frontend/src/app/broken.ts", Assert.Single(finding.Locations).Path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Prompt_projection_is_compact_bounded_and_full_diagnostics_remain_standalone()
    {
        var findings = Enumerable.Range(1, 40).Select(index => new ReviewFinding(
            $"angular-ng8102-{index}",
            "compiler",
            FindingSeverity.Medium,
            "NG8102 warning",
            new string('x', 500),
            new string('r', 500),
            [new FindingLocation(
                $"frontend/src/app/editor/editor-{index:D2}.html",
                new FindingRange(new FindingPosition(171, 61), new FindingPosition(171, 89)))],
            $"sha256:{index:x64}",
            "NG8102",
            Source: new FindingSource(
                FindingSourceKind.Deterministic, "angular-compiler", "AngularCompiler", "20.3.29")))
            .ToArray();
        var evidence = new SensorScanResult(
            true,
            null,
            findings,
            new SensorProvenance(
                "angular-compiler", AngularCompilerSensor.SensorVersion, "repository", ".",
                DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string, string> { ["angularCompiler"] = "20.3.29" }));

        var projection = DeterministicEvidenceProjection.ToPromptJson([evidence]);
        using var json = JsonDocument.Parse(projection);

        Assert.InRange(projection.Length, 1,
            DeterministicEvidenceProjection.MaximumPromptCharacters);
        var projectedSensor = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("angular-compiler", projectedSensor.GetProperty("sensorId").GetString());
        Assert.Equal("AngularCompiler", projectedSensor.GetProperty("producer").GetString());
        Assert.StartsWith("sha256:", projectedSensor.GetProperty("resultHash").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(projectedSensor.GetProperty("findings").EnumerateArray(), finding =>
            finding.GetProperty("ruleId").GetString() == "NG8102" &&
            finding.GetProperty("path").GetString()!.StartsWith(
                "frontend/src/app/editor/", StringComparison.Ordinal));
        Assert.True(projectedSensor.GetProperty("omittedFindings").GetInt32() > 0);
        Assert.DoesNotContain(new string('x', 100), projection, StringComparison.Ordinal);
        Assert.DoesNotContain("description", projection, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new string('x', 500), findings[0].Description);
    }

    private static async Task CreateAngularTargetAsync(
        string root,
        bool includeCompiler,
        CancellationToken cancellationToken)
    {
        var frontend = Path.Combine(root, "frontend");
        Directory.CreateDirectory(frontend);
        await File.WriteAllTextAsync(
            Path.Combine(frontend, "tsconfig.app.json"), "{}", cancellationToken);
        if (!includeCompiler) return;

        var compilerPackage = Path.Combine(frontend, "node_modules", "@angular", "compiler-cli");
        var typeScriptPackage = Path.Combine(frontend, "node_modules", "typescript");
        Directory.CreateDirectory(Path.Combine(compilerPackage, "bundles", "src", "bin"));
        Directory.CreateDirectory(typeScriptPackage);
        await File.WriteAllTextAsync(
            Path.Combine(compilerPackage, "bundles", "src", "bin", "ngc.js"),
            "#!/usr/bin/env node", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(compilerPackage, "package.json"),
            "{\"version\":\"20.3.29\"}", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(typeScriptPackage, "package.json"),
            "{\"version\":\"5.9.3\"}", cancellationToken);
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
