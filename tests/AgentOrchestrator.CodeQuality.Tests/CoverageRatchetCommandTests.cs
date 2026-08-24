using AgentOrchestrator.CodeQuality;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class CoverageRatchetCommandTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("quality-coverage-ratchet-").FullName;
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    private string BaselinePath => Path.Combine(root, ".quality", "coverage-baseline.json");

    [Fact]
    public async Task Update_records_the_first_measured_baseline_per_area()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);
        var lcov = WriteLcov("frontend.lcov.info", coveredLines: 1, uncoveredLines: 1);

        var exitCode = await Run(root, "--report", cobertura, "--report", lcov, "--update");

        Assert.Equal(CoverageRatchetCommand.SuccessExitCode, exitCode);
        var baseline = ReadBaseline();
        Assert.Equal(CoverageRatchetBaseline.CurrentSchemaVersion, baseline.SchemaVersion);
        Assert.Equal(75.00m, Area(baseline, "api").LinePercent);
        Assert.Equal(50.00m, Area(baseline, "frontend").LinePercent);
        // The catch-all area proves nothing was dropped by a non-matching prefix.
        Assert.Equal(6, Area(baseline, "repository").TotalLines);
        Assert.Contains("recorded baseline", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unchanged_coverage_passes_the_ratchet()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);
        var lcov = WriteLcov("frontend.lcov.info", coveredLines: 1, uncoveredLines: 1);
        Assert.Equal(0, await Run(root, "--report", cobertura, "--report", lcov, "--update"));

        var exitCode = await Run(root, "--report", cobertura, "--report", lcov);

        Assert.Equal(CoverageRatchetCommand.SuccessExitCode, exitCode);
        Assert.Contains("no area fell below the baseline", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decrease_beyond_the_tolerance_fails_the_gate()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 4, uncoveredApiLines: 0);
        var lcov = WriteLcov("frontend.lcov.info", coveredLines: 1, uncoveredLines: 1);
        Assert.Equal(0, await Run(root, "--report", cobertura, "--report", lcov, "--update"));

        WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 2, uncoveredApiLines: 2);
        var exitCode = await Run(root, "--report", cobertura, "--report", lcov);

        Assert.Equal(CoverageRatchetCommand.RegressionExitCode, exitCode);
        Assert.Contains("area 'api' fell from 100.00% to 50.00%", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_increase_passes_and_can_raise_the_baseline()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 2, uncoveredApiLines: 2);
        var lcov = WriteLcov("frontend.lcov.info", coveredLines: 1, uncoveredLines: 1);
        Assert.Equal(0, await Run(root, "--report", cobertura, "--report", lcov, "--update"));

        WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 4, uncoveredApiLines: 0);
        Assert.Equal(CoverageRatchetCommand.SuccessExitCode, await Run(root, "--report", cobertura, "--report", lcov));
        Assert.Equal(0, await Run(root, "--report", cobertura, "--report", lcov, "--update"));

        Assert.Equal(100.00m, Area(ReadBaseline(), "api").LinePercent);
    }

    [Fact]
    public async Task A_report_that_stopped_being_produced_fails_instead_of_reading_as_zero_coverage()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);
        var lcov = WriteLcov("frontend.lcov.info", coveredLines: 1, uncoveredLines: 1);
        Assert.Equal(0, await Run(root, "--report", cobertura, "--report", lcov, "--update"));

        var exitCode = await Run(root, "--report", cobertura);

        Assert.Equal(CoverageRatchetCommand.RegressionExitCode, exitCode);
        Assert.Contains("area 'frontend' measured 0 lines", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_report_file_is_an_error_not_an_empty_measurement()
    {
        var exitCode = await Run(root, "--report", Path.Combine(root, "never-generated.cobertura.xml"));

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, exitCode);
        Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_results_directory_is_expanded_to_the_reports_inside_it()
    {
        // dotnet test writes coverage under a generated per-run subdirectory, so the gate
        // has to be able to point at the results directory rather than at exact file names.
        var results = Directory.CreateDirectory(Path.Combine(root, ".coverage", Guid.NewGuid().ToString("N"))).FullName;
        var cobertura = WriteCobertura(Path.Combine(results, "coverage.cobertura.xml"), coveredApiLines: 3, uncoveredApiLines: 1);
        WriteLcov(Path.Combine(results, "lcov.info"), coveredLines: 1, uncoveredLines: 1);

        Assert.Equal(0, await Run(root, "--report", Path.Combine(root, ".coverage"), "--update"));

        Assert.Equal(75.00m, Area(ReadBaseline(), "api").LinePercent);
        Assert.Equal(50.00m, Area(ReadBaseline(), "frontend").LinePercent);
        Assert.Contains(cobertura, CoverageRatchetCommand.ExpandReports([Path.Combine(root, ".coverage")]));
    }

    [Fact]
    public void A_results_directory_without_any_report_is_an_error()
    {
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty-results")).FullName;

        var failure = Assert.Throws<FileNotFoundException>(() => CoverageRatchetCommand.ExpandReports([empty]));

        Assert.Contains("contains no", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_baseline_is_an_error()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        await File.WriteAllTextAsync(BaselinePath, "{ not json", TestContext.Current.CancellationToken);

        var exitCode = await Run(root, "--report", cobertura);

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, exitCode);
        Assert.Contains("is unreadable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enforcing_without_a_recorded_baseline_is_an_error()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);

        var exitCode = await Run(root, "--report", cobertura);

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, exitCode);
        Assert.Contains("Record the first measured baseline with --update", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_refuses_to_overwrite_a_baseline_it_cannot_read()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);
        Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
        await File.WriteAllTextAsync(BaselinePath, "<<<<<<< HEAD", TestContext.Current.CancellationToken);

        var exitCode = await Run(root, "--report", cobertura, "--update");

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, exitCode);
        Assert.Contains("is unreadable", error.ToString(), StringComparison.Ordinal);
        // The recorded areas and tolerance must survive; silently resetting them would
        // discard the ratchet while reporting success.
        Assert.Equal("<<<<<<< HEAD", await File.ReadAllTextAsync(BaselinePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_report_arguments_are_rejected()
    {
        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, await Run(root));
        Assert.Contains("at least one --report is required", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_option_value_that_looks_like_a_flag_is_rejected()
    {
        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, await Run(root, "--report", "--update"));
        Assert.Contains("'--report' requires a value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_negative_tolerance_is_rejected_rather_than_inverting_the_comparison()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, await Run(root, "--report", cobertura, "--tolerance", "-5"));
        Assert.Contains("must not be negative", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tolerance_that_is_not_a_number_says_so()
    {
        var cobertura = WriteCobertura("dotnet.cobertura.xml", coveredApiLines: 3, uncoveredApiLines: 1);

        Assert.Equal(CoverageRatchetCommand.ErrorExitCode, await Run(root, "--report", cobertura, "--tolerance", "abc"));
        Assert.Contains("expects a number, not 'abc'", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Measure_assigns_files_to_every_matching_area()
    {
        IReadOnlyList<CoverageFile> files =
        [
            new("src/QualityStudio.Api/Program.cs", 3, 4, 0, 0, [], []),
            new("frontend/src/app/app.ts", 1, 2, 0, 0, [], []),
        ];

        var measured = CoverageRatchetCommand.Measure(files, CoverageRatchetBaseline.DefaultAreas);

        Assert.Equal(75.00m, measured.Single(area => area.Id == "api").LinePercent);
        Assert.Equal(50.00m, measured.Single(area => area.Id == "frontend").LinePercent);
        Assert.Equal(0, measured.Single(area => area.Id == "core").TotalLines);
        Assert.Equal(6, measured.Single(area => area.Id == "repository").TotalLines);
    }

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        TestDirectory.Delete(root);
    }

    private Task<int> Run(params string[] args) =>
        CoverageRatchetCommand.RunAsync(args, output, error, TestContext.Current.CancellationToken);

    private CoverageRatchetBaseline ReadBaseline()
    {
        var baseline = System.Text.Json.JsonSerializer.Deserialize<CoverageRatchetBaseline>(
            File.ReadAllText(BaselinePath),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Assert.IsType<CoverageRatchetBaseline>(baseline);
    }

    private static CoverageRatchetArea Area(CoverageRatchetBaseline baseline, string id) =>
        Assert.Single(baseline.Areas, area => area.Id == id);

    private string WriteCobertura(string name, int coveredApiLines, int uncoveredApiLines)
    {
        var lines = string.Concat(
            Enumerable.Range(1, coveredApiLines).Select(number => $"            <line number=\"{number}\" hits=\"1\" />\n")
                .Concat(Enumerable.Range(coveredApiLines + 1, uncoveredApiLines)
                    .Select(number => $"            <line number=\"{number}\" hits=\"0\" />\n")));
        var path = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0">
              <packages>
                <package name="QualityStudio.Api">
                  <classes>
                    <class name="Program" filename="src/QualityStudio.Api/Program.cs">
                      <lines>
            {lines}          </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);
        return path;
    }

    private string WriteLcov(string name, int coveredLines, int uncoveredLines)
    {
        var records = string.Concat(
            Enumerable.Range(1, coveredLines).Select(number => $"DA:{number},1\n")
                .Concat(Enumerable.Range(coveredLines + 1, uncoveredLines).Select(number => $"DA:{number},0\n")));
        var path = Path.IsPathRooted(name) ? name : Path.Combine(root, name);
        File.WriteAllText(path, $"TN:\nSF:frontend/src/app/app.ts\n{records}end_of_record\n");
        return path;
    }
}
