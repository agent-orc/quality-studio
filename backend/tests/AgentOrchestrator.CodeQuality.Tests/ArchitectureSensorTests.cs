using System.Text.Json;
using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

public sealed class ArchitectureSensorTests
{
    [Fact]
    public async Task Repositories_without_a_contract_are_not_judged_by_Quality_Studios_layout()
    {
        using var repository = new Fixture();
        repository.Write("src/EntirelyValidAlternative.cs", "class Alternative;");
        var result = await repository.Scan();
        Assert.False(result.Available);
        Assert.Contains("No repository architecture contract", result.UnavailableReason);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Declared_layout_detects_retired_source_missing_directory_and_misplaced_feature()
    {
        using var repository = new Fixture();
        repository.Contract(required: ["backend/src", "backend/tests"], forbidden: ["src"],
            rules: [new ArchitectureDirectoryRule("frontend/src/app", ["core", "shared", "features", "shell"], ["app.config.ts"])]);
        repository.Write("backend/src/Api/Program.cs", "class Api;");
        repository.Write("src/OldApi/Program.cs", "class OldApi;");
        repository.Write("frontend/src/app/dashboard/dashboard.ts", "export class Dashboard {}");
        var result = await repository.Scan();

        Assert.True(result.Available);
        Assert.Equal(3, result.Findings.Count);
        Assert.Contains(result.Findings, finding => finding.RuleId == "architecture/missing-directory" &&
            finding.Description.Contains("backend/tests", StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.RuleId == "architecture/forbidden-source-path" &&
            finding.Locations.Single().Path == "src/OldApi/Program.cs");
        Assert.Contains(result.Findings, finding => finding.RuleId == "architecture/unexpected-entry" &&
            finding.Locations.Single().Path == "frontend/src/app/dashboard/dashboard.ts");
        Assert.All(result.Findings, finding =>
        {
            Assert.Equal(FindingSourceKind.Deterministic, finding.Source!.Kind);
            Assert.Equal("architecture", finding.Source.SensorId);
            Assert.Equal(FindingSeverity.Medium, finding.Severity);
            Assert.NotEmpty(finding.Recommendation);
        });
    }

    [Fact]
    public async Task Generated_build_output_and_historical_review_metadata_do_not_count_as_retired_source()
    {
        using var repository = new Fixture();
        repository.Contract(forbidden: ["src"],
            rules: [new ArchitectureDirectoryRule("frontend/src/app", ["core"], ["app.config.ts"])]);
        repository.Write("src/OldApi/bin/Debug/Api.dll", "generated");
        repository.Write("src/OldApi/obj/project.assets.json", "{}");
        repository.Write("src/OldApi/.quality/review.json", "{}");
        repository.Write("frontend/src/app/.quality/review.json", "{}");
        repository.Write("frontend/src/app/core/client.ts", "export class Client {}");
        repository.Write("frontend/src/app/app.config.ts", "export const config = {};");
        var result = await repository.Scan();
        Assert.True(result.Available);
        Assert.Empty(result.Findings);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/outside")]
    [InlineData("backend/../outside")]
    [InlineData("backend\\src")]
    [InlineData("backend//src")]
    public async Task Contract_paths_cannot_escape_or_ambiguously_address_the_repository(string path)
    {
        using var repository = new Fixture();
        repository.Contract(required: [path]);
        var finding = Assert.Single((await repository.Scan()).Findings);
        Assert.Equal("architecture/invalid-contract", finding.RuleId);
        Assert.Equal(ArchitectureSensor.ContractFileName, finding.Locations.Single().Path);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\": 99, \"requiredDirectories\": [], \"forbiddenSourcePaths\": [], \"directoryRules\": []}")]
    [InlineData("{\"schemaVersion\": 1, \"requiredDirectories\": null, \"forbiddenSourcePaths\": [], \"directoryRules\": []}")]
    [InlineData("{\"schemaVersion\": 1, \"requiredDirectories\": [], \"forbiddenSourcePaths\": [], \"directoryRules\": [], \"typo\": []}")]
    public async Task Invalid_contract_is_a_visible_finding_and_never_a_false_clean_scan(string content)
    {
        using var repository = new Fixture();
        repository.Write(ArchitectureSensor.ContractFileName, content);
        var result = await repository.Scan();
        Assert.True(result.Available);
        Assert.Equal("architecture/invalid-contract", Assert.Single(result.Findings).RuleId);
    }

    [Fact]
    public async Task Oversized_contract_is_rejected_before_deserialization()
    {
        using var repository = new Fixture();
        repository.Write(ArchitectureSensor.ContractFileName, new string(' ', 128 * 1024 + 1));
        Assert.Equal("architecture/invalid-contract", Assert.Single((await repository.Scan()).Findings).RuleId);
    }

    [Fact]
    public async Task Fingerprint_tracks_the_boundary_violation_across_file_content_edits()
    {
        using var repository = new Fixture();
        repository.Contract(forbidden: ["src"]);
        repository.Write("src/Old.cs", "class First;");
        var before = Assert.Single((await repository.Scan()).Findings);
        repository.Write("src/Old.cs", "class Second;");
        var after = Assert.Single((await repository.Scan()).Findings);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    [Fact]
    public async Task Real_Quality_Studio_layout_satisfies_its_own_contract()
    {
        var root = RepositoryTestContext.FindRepositoryRoot();
        Assert.True(ArchitectureSensor.HasTarget(root));
        var result = await new ArchitectureSensor().RunAsync(
            new SensorScanRequest(root, PersistMetadata: false), TestContext.Current.CancellationToken);
        Assert.True(result.Available, result.UnavailableReason);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Architecture_findings_enter_the_real_review_evidence_collector_and_analysis_runner()
    {
        using var repository = new Fixture();
        repository.Contract(forbidden: ["src"]);
        repository.Write("src/Old.cs", "class Old;");
        var collector = new DeterministicEvidenceCollector(new SensorRegistry([new ArchitectureSensor()]));
        var evidence = await collector.CollectAsync(repository.Root,
            [new ReviewSensorConfiguration("architecture")], TestContext.Current.CancellationToken);
        var projected = DeterministicEvidenceProjection.ForSubjects(evidence, ["src/Old.cs"]);
        Assert.Equal("architecture/forbidden-source-path", Assert.Single(Assert.Single(projected).Findings).RuleId);
        var analysis = await new QualityAnalysisRunner().RunAsync(new QualityAnalysisRequest(
            repository.Root, [new QualityAnalysisDefinition(QualityAnalysisNames.Architecture)]),
            TestContext.Current.CancellationToken);
        Assert.Equal("architecture/forbidden-source-path", Assert.Single(analysis.Findings).RuleId);
        Assert.True(Assert.Single(analysis.Analyses).Available);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("quality-architecture-").FullName;
        public void Write(string path, string content)
        {
            var absolute = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, content);
        }

        public void Contract(string[]? required = null, string[]? forbidden = null, ArchitectureDirectoryRule[]? rules = null) =>
            Write(ArchitectureSensor.ContractFileName, JsonSerializer.Serialize(
                new ArchitectureContract(1, required ?? [], forbidden ?? [], rules ?? []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public Task<SensorScanResult> Scan() => new ArchitectureSensor().RunAsync(
            new SensorScanRequest(Root, PersistMetadata: false), TestContext.Current.CancellationToken);
        public void Dispose() => TemporaryDirectory.Delete(Root);
    }
}
