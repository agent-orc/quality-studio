using System.Text.Json;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class AngularCompilerSensorTests
{
    [Fact]
    public void Parse_normalizes_ng8102_at_exact_location_with_stable_fingerprint()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-parser"));
        var output = """
            src/app/editor/editor.html:169:61 - warning NG8102: The left side does not include 'null' or 'undefined'.

            169                   <input [value]="drafts()[row.thread.id] ?? ''">
                                                                ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
            """;

        var finding = Assert.Single(AngularCompilerSensor.Parse(output, root, "20.3.29"));

        Assert.Equal("NG8102", finding.RuleId);
        Assert.Equal("compiler", finding.Aspect);
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("frontend/src/app/editor/editor.html", Assert.Single(finding.Locations).Path);
        Assert.Equal(new FindingPosition(169, 61), finding.Locations[0].Range!.Start);
        Assert.Equal(new FindingPosition(169, 89), finding.Locations[0].Range!.End);
        Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
        Assert.Equal("angular-compiler", finding.Source.SensorId);
        Assert.Equal("AngularCompiler", finding.Source.Producer);
        Assert.Equal("20.3.29", finding.Source.ProducerVersion);

        var repeated = Assert.Single(AngularCompilerSensor.Parse(output, root, "20.3.29"));
        Assert.Equal(finding.Fingerprint, repeated.Fingerprint);
    }

    [Fact]
    public void Parse_normalizes_typescript_diagnostics_emitted_by_ngc()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "quality-studio-angular-parser"));
        var output = "src/app/editor/editor.ts(24,16): error TS2322: Type 'string' is not assignable.";

        var finding = Assert.Single(AngularCompilerSensor.Parse(output, root, "20.3.29"));

        Assert.Equal("TS2322", finding.RuleId);
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Equal("frontend/src/app/editor/editor.ts", Assert.Single(finding.Locations).Path);
        Assert.Equal(new FindingPosition(24, 16), finding.Locations[0].Range!.Start);
    }

    [Fact]
    public async Task Availability_requires_project_and_runnable_repository_local_compiler()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-availability-").FullName;
        try
        {
            await AddAngularTargetAsync(root);
            var runner = new QueueRunner(new SensorCommandResult(0, "0.0.0", string.Empty));

            var available = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);

            Assert.True(available.Available);
            Assert.Equal("20.3.29", available.ToolVersions!["angularCompiler"]);
            Assert.Equal("node", Assert.Single(runner.Calls).Executable);
            Assert.EndsWith("/ngc.js", runner.Calls[0].Arguments[0], StringComparison.Ordinal);

            File.Delete(Path.Combine(root, "frontend", "tsconfig.app.json"));
            var missingProject = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);
            AssertUnavailable(missingProject, "tsconfig.app.json");

            await File.WriteAllTextAsync(Path.Combine(root, "frontend", "tsconfig.app.json"), "{}",
                TestContext.Current.CancellationToken);
            File.Delete(Path.Combine(root, "frontend", "node_modules", "@angular", "compiler-cli",
                "bundles", "src", "bin", "ngc.js"));
            var missingCompiler = await new AngularCompilerSensor(runner, root)
                .ProbeAvailabilityAsync(TestContext.Current.CancellationToken);
            AssertUnavailable(missingCompiler, "repository-local");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Failed_compilation_without_parseable_diagnostics_is_unavailable_not_clean()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-run-").FullName;
        try
        {
            await AddAngularTargetAsync(root);
            var runner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(1, string.Empty, "Compilation failed."));

            var result = await new AngularCompilerSensor(runner).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.False(result.Available);
            Assert.Empty(result.Findings);
            Assert.Contains("without parseable diagnostics", result.UnavailableReason,
                StringComparison.Ordinal);
            Assert.True(result.UnavailableReason!.Length <= 1000);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Run_surfaces_every_diagnostic_emitted_by_compiler()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-angular-run-").FullName;
        try
        {
            await AddAngularTargetAsync(root);
            var output = """
                src/app/editor/editor.html:169:61 - warning NG8102: First template diagnostic.
                169 <input [value]="first ?? ''">
                              ~~~~~~~~~~~
                src/app/editor/editor.html:174:86 - warning NG8102: Second template diagnostic.
                174 <textarea [value]="second ?? ''">
                                               ~~~~~~~~~~~~~
                """;
            var emitted = AngularCompilerSensor.Parse(output, root, "20.3.29");
            var runner = new QueueRunner(
                new SensorCommandResult(0, "0.0.0", string.Empty),
                new SensorCommandResult(0, output, string.Empty));

            var result = await new AngularCompilerSensor(runner).RunAsync(
                new SensorScanRequest(root), TestContext.Current.CancellationToken);

            Assert.True(result.Available);
            Assert.Equal(emitted.Select(finding => finding.Fingerprint),
                result.Findings.Select(finding => finding.Fingerprint));
            Assert.All(result.Findings, finding => Assert.Equal("NG8102", finding.RuleId));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Prompt_projection_is_compact_bounded_and_excludes_full_diagnostics()
    {
        var findings = Enumerable.Range(1, 40).Select(index => new ReviewFinding(
            $"angular-ng8102-{index}",
            "compiler",
            FindingSeverity.Medium,
            "NG8102: compact title",
            new string('d', 400),
            new string('r', 400),
            [new FindingLocation(
                $"frontend/src/app/editor/editor-{index:D2}.html",
                new FindingRange(new FindingPosition(169, 61), new FindingPosition(169, 89)))],
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
                "2026-09-01T00:00:00Z",
                new Dictionary<string, string> { ["angularCompiler"] = "20.3.29" }));

        var promptJson = DeterministicEvidenceProjection.ToPromptJson([evidence]);

        Assert.True(promptJson.Length <= DeterministicEvidenceProjection.MaximumPromptCharacters);
        Assert.DoesNotContain(new string('d', 100), promptJson, StringComparison.Ordinal);
        Assert.DoesNotContain("recommendation", promptJson, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(promptJson);
        var projection = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("angular-compiler", projection.GetProperty("sensorId").GetString());
        Assert.Matches("^sha256:[a-f0-9]{64}$", projection.GetProperty("resultHash").GetString());
        Assert.True(projection.GetProperty("omittedFindings").GetInt32() > 0);
        var first = projection.GetProperty("findings")[0];
        Assert.Equal("NG8102", first.GetProperty("ruleId").GetString());
        Assert.Equal("medium", first.GetProperty("severity").GetString());
        Assert.Equal(169, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    internal static async Task AddAngularTargetAsync(string root)
    {
        var compilerDirectory = Path.Combine(root, "frontend", "node_modules", "@angular",
            "compiler-cli");
        var binaryDirectory = Path.Combine(compilerDirectory, "bundles", "src", "bin");
        Directory.CreateDirectory(binaryDirectory);
        await File.WriteAllTextAsync(Path.Combine(root, "frontend", "tsconfig.app.json"), "{}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(binaryDirectory, "ngc.js"), "// fixture",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(compilerDirectory, "package.json"),
            "{\"version\":\"20.3.29\"}", TestContext.Current.CancellationToken);
    }

    private static void AssertUnavailable(SensorAvailability availability, string reasonFragment)
    {
        Assert.False(availability.Available);
        Assert.Contains(reasonFragment, availability.UnavailableReason, StringComparison.Ordinal);
        Assert.True(availability.UnavailableReason!.Length <= 1000);
    }

    internal sealed class QueueRunner(params SensorCommandResult[] results) : ISensorCommandRunner
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

    internal sealed record Call(string Executable, IReadOnlyList<string> Arguments);
}
