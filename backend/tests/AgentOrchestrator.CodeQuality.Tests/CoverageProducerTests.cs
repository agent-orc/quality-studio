using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// The coverage producer runs a host-owned test command into the data root, time-boxed, and the
/// coverage sensor ingests what it wrote. A fake runner stands in for <c>dotnet test</c> and vitest.
/// </summary>
public sealed class CoverageProducerTests
{
    private const string Cobertura = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="0">
          <packages><package name="Fixture"><classes>
            <class name="Calculator" filename="{0}">
              <lines><line number="1" hits="1" /><line number="2" hits="0" /></lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    [Fact]
    public async Task Dotnet_profile_writes_into_the_data_root_and_the_sensor_ingests_it()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner((arguments, _) =>
        {
            // `dotnet test` writes one GUID directory per test project below --results-directory.
            var output = arguments[arguments.IndexOf("--results-directory") + 1];
            var run = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(run, "coverage.cobertura.xml"),
                Cobertura.Replace("{0}", Path.Combine(fixture.Root, "src", "Calculator.cs")));
            return new SensorCommandResult(1, "Failed: 1, Passed: 4", string.Empty);
        });
        var sensor = new CoverageSensor(producer: new CoverageProducer(runnerFactory: runner.Create));

        var result = await sensor.RunAsync(new SensorScanRequest(fixture.Root,
            Configuration: new Dictionary<string, string> { ["profile"] = "dotnet-test-coverage" }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Available, result.UnavailableReason);
        Assert.Equal("dotnet", runner.Executable);
        Assert.Equal(["test", fixture.Root], runner.Arguments!.Take(2));
        Assert.Contains("Code Coverage;Format=cobertura", runner.Arguments!);
        Assert.Equal(TimeSpan.FromSeconds(900), runner.Timeout);
        var outputDirectory = runner.Arguments![runner.Arguments.IndexOf("--results-directory") + 1];
        Assert.StartsWith(QualityDataRoot.For(fixture.Root), outputDirectory, StringComparison.Ordinal);
        Assert.False(outputDirectory.StartsWith(fixture.Root, StringComparison.Ordinal));

        var snapshot = CoverageSnapshot.Load(fixture.Root)!;
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("src/Calculator.cs", file.Path);
        Assert.Equal(1, file.CoveredLines);
        var report = Assert.Single(snapshot.Reports);
        Assert.StartsWith("data-root:coverage/produced/dotnet-test-coverage/", report, StringComparison.Ordinal);
        // A failing test run still measured coverage; the exit code is recorded, not fatal.
        Assert.Equal(1, snapshot.Production!.ExitCode);
        Assert.Equal("1", result.Provenance.ToolVersions["producerExitCode"]);
    }

    [Fact]
    public async Task Vitest_lcov_paths_resolve_against_the_profile_working_directory()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "frontend", "src"));
        File.WriteAllText(Path.Combine(fixture.Root, "frontend", "src", "app.ts"), "export const a = 1;\n");
        var runner = new FakeRunner((arguments, workingDirectory) =>
        {
            Assert.Equal(Path.Combine(fixture.Root, "frontend"), workingDirectory);
            var output = arguments.Single(argument => argument.StartsWith("--coverage.reportsDirectory=", StringComparison.Ordinal))
                ["--coverage.reportsDirectory=".Length..];
            File.WriteAllText(Path.Combine(output, "lcov.info"), "SF:src/app.ts\nDA:1,3\nend_of_record\n");
            return new SensorCommandResult(0, string.Empty, string.Empty);
        });
        var sensor = new CoverageSensor(producer: new CoverageProducer(runnerFactory: runner.Create));

        var result = await sensor.RunAsync(new SensorScanRequest(fixture.Root,
            Configuration: new Dictionary<string, string> { ["profile"] = "vitest-frontend-coverage" }),
            TestContext.Current.CancellationToken);

        Assert.True(result.Available, result.UnavailableReason);
        Assert.Equal("npx", runner.Executable);
        Assert.Equal(TimeSpan.FromSeconds(600), runner.Timeout);
        var file = Assert.Single(CoverageSnapshot.Load(fixture.Root)!.Files);
        Assert.Equal("frontend/src/app.ts", file.Path);
        Assert.Equal(100m, CoverageProjection.ForPath(CoverageSnapshot.Load(fixture.Root), null, file.Path, file: true).LinePercent);
    }

    [Fact]
    public async Task A_producer_that_times_out_keeps_the_last_snapshot()
    {
        using var fixture = new Fixture();
        var previous = new CoverageSnapshot(1, CoverageSensor.CurrentVersion, "2026-09-01T00:00:00Z", "abc",
            ["coverage.xml"], [new CoverageFile("src/Calculator.cs", 1, 1, 0, 0, [], [])]);
        await previous.SaveAsync(fixture.Root, TestContext.Current.CancellationToken);
        var runner = new FakeRunner((_, _) =>
            throw new SecurityScannerUnavailableException("dotnet timed out after 00:15:00."));
        var sensor = new CoverageSensor(producer: new CoverageProducer(runnerFactory: runner.Create));

        var result = await sensor.RunAsync(new SensorScanRequest(fixture.Root,
            Configuration: new Dictionary<string, string> { ["profile"] = "dotnet-test-coverage" }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Available);
        Assert.Contains("timed out", result.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal("abc", CoverageSnapshot.Load(fixture.Root)!.Commit);
    }

    [Fact]
    public async Task A_producer_that_writes_no_report_is_unavailable_with_its_output()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner((_, _) => new SensorCommandResult(1, "error MSB1003: no project", string.Empty));
        var sensor = new CoverageSensor(producer: new CoverageProducer(runnerFactory: runner.Create));

        var result = await sensor.RunAsync(new SensorScanRequest(fixture.Root,
            Configuration: new Dictionary<string, string> { ["profile"] = "dotnet-test-coverage" }),
            TestContext.Current.CancellationToken);

        Assert.False(result.Available);
        Assert.Contains("wrote no report matching '**/*.cobertura.xml'", result.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("MSB1003", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_host_profile_can_produce_and_a_stale_report_never_survives_a_run()
    {
        using var fixture = new Fixture();
        var runner = new FakeRunner((_, _) => new SensorCommandResult(0, string.Empty, string.Empty));
        var producer = new CoverageProducer(runnerFactory: runner.Create);
        var stale = QualityDataRoot.Combine(fixture.Root, "coverage", "produced", "dotnet-test-coverage", "old");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "coverage.cobertura.xml"), Cobertura.Replace("{0}", "src/Calculator.cs"));

        var unknown = await producer.ProduceAsync(fixture.Root,
            new Dictionary<string, string> { ["profile"] = "rm-rf" }, TestContext.Current.CancellationToken);
        var inline = await producer.ProduceAsync(fixture.Root,
            new Dictionary<string, string> { ["command"] = "dotnet test" }, TestContext.Current.CancellationToken);
        var escaping = await producer.ProduceAsync(fixture.Root,
            new Dictionary<string, string> { ["profile"] = "dotnet-test-coverage", ["target"] = "../elsewhere" },
            TestContext.Current.CancellationToken);
        var fresh = await producer.ProduceAsync(fixture.Root,
            new Dictionary<string, string> { ["profile"] = "dotnet-test-coverage" }, TestContext.Current.CancellationToken);

        Assert.Contains("not configured for sensor 'coverage'", unknown.Refusal, StringComparison.Ordinal);
        Assert.Contains("host-owned", inline.Refusal, StringComparison.Ordinal);
        Assert.Contains("inside the repository", escaping.Refusal, StringComparison.Ordinal);
        Assert.Empty(fresh.Reports);
        Assert.False(Directory.Exists(stale));
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Built_in_catalogue_offers_coverage_profiles_with_a_time_box()
    {
        var coverage = AnalyzerProfileCatalog.BuiltIn.ForSensor("coverage");

        Assert.Equal(["dotnet-test-coverage", "vitest-frontend-coverage", "vitest-root-coverage"],
            coverage.Select(profile => profile.Id));
        Assert.All(coverage, profile => Assert.InRange(profile.TimeoutSeconds!.Value, 60, 3600));
        Assert.All(coverage, profile => Assert.Contains("{outputDirectory}", profile.Command, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => new AnalyzerProfileCatalog(
            [new AnalyzerProfile("slow", "coverage", "dotnet test", TimeoutSeconds: 7200)]));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Directory.CreateTempSubdirectory("quality-studio-producer-").FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "Calculator.cs"), "class Calculator {}\n");
        }

        public string Root { get; }

        public void Dispose()
        {
            TemporaryDirectory.Delete(Root);
            var data = QualityDataRoot.For(Root);
            if (Directory.Exists(data)) TemporaryDirectory.Delete(data);
        }
    }

    private sealed class FakeRunner(Func<List<string>, string, SensorCommandResult> behaviour) : ISensorCommandRunner
    {
        public TimeSpan? Timeout { get; private set; }
        public string? Executable { get; private set; }
        public List<string>? Arguments { get; private set; }
        public int Calls { get; private set; }

        public ISensorCommandRunner Create(TimeSpan timeout)
        {
            Timeout = timeout;
            return this;
        }

        public Task<SensorCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, CancellationToken cancellationToken = default)
        {
            Calls++;
            Executable = executable;
            Arguments = [.. arguments];
            return Task.FromResult(behaviour(Arguments, workingDirectory));
        }
    }
}
