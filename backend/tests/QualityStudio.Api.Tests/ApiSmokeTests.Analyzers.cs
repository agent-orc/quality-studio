using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrchestrator.CodeQuality;
using QualityStudio.Testing;
using Xunit;

namespace QualityStudio.Api.Tests;

[Trait("Category", "ToolBound")]
public sealed partial class ApiSmokeTests
{
    [Fact]
    public async Task An_analyzer_scan_is_persisted_and_served_to_the_explorer_and_editor_with_its_catalogue_rule()
    {
        var analyzedRoot = repositoryRoot + "-analyzers";
        Directory.CreateDirectory(Path.Combine(analyzedRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(analyzedRoot, "Analyzed.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(analyzedRoot, "src", "Reader.cs"),
            "namespace Analyzed;\npublic sealed class Reader\n{\n    public async Task Read(Stream s, CancellationToken token) => await s.ReadAsync(new byte[1]);\n}\n",
            TestContext.Current.CancellationToken);
        // A repository-produced report: the SARIF sensor imports it without running anything.
        Directory.CreateDirectory(Path.Combine(analyzedRoot, "reports"));
        await File.WriteAllTextAsync(Path.Combine(analyzedRoot, "reports", "roslyn.sarif"), """
            {"version":"2.1.0","runs":[{"tool":{"driver":{"name":"Microsoft.CodeAnalysis","version":"5.6.0"}},
              "invocations":[{"executionSuccessful":true}],
              "results":[
                {"ruleId":"CA2016","level":"warning","message":{"text":"Forward the 'token' parameter to 'ReadAsync'."},
                 "locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/Reader.cs"},"region":{"startLine":4,"startColumn":71}}}]},
                {"ruleId":"CA1822","level":"warning","message":{"text":"Suppressed."},"suppressions":[{"kind":"inSource"}],
                 "locations":[{"physicalLocation":{"artifactLocation":{"uri":"src/Reader.cs"},"region":{"startLine":4,"startColumn":5}}}]}]}]}
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(analyzedRoot, ".gitignore"), "reports/\n", TestContext.Current.CancellationToken);
        await GitTestRepository.InitializeAsync(analyzedRoot, TestContext.Current.CancellationToken);
        try
        {
            using var client = application!.CreateClient();
            using var created = await client.PostAsJsonAsync("/api/repos", new
            {
                id = "analyzed",
                displayName = "Analyzed",
                rootPath = analyzedRoot,
                inputBudgetCharacters = 8000,
                enabledReviewKinds = new[] { "code" },
                sensors = new object[]
                {
                    new { id = "sarif", enabled = true, configuration = new { reportPath = "reports/roslyn.sarif" } },
                },
            }, TestContext.Current.CancellationToken);
            Assert.True(created.StatusCode == HttpStatusCode.Created,
                await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            using var scan = await client.PostAsync("/api/repos/analyzed/sensors/sarif/scan", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
            var scanned = await scan.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(1, scanned.GetProperty("suppressedFindings").GetInt32());
            Assert.True(File.Exists(Path.Combine(QualityDataRoot.For(analyzedRoot), "analyzers", "sarif.json")));
            Assert.False(Directory.Exists(Path.Combine(analyzedRoot, ".quality", "analyzers")));

            using var counts = await client.GetAsync("/api/repos/analyzed/analyzers/counts", TestContext.Current.CancellationToken);
            var countsJson = await counts.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(1, countsJson.GetProperty("files").GetProperty("src/Reader.cs").GetInt32());

            using var folder = await client.GetAsync("/api/repos/analyzed/analyzers?path=src", TestContext.Current.CancellationToken);
            var folderJson = await folder.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var entry = Assert.Single(folderJson.GetProperty("findings").EnumerateArray());
            Assert.Equal("CA2016", entry.GetProperty("finding").GetProperty("ruleId").GetString());
            var rule = Assert.Single(entry.GetProperty("catalogueRules").EnumerateArray());
            Assert.Equal("QS-CS-003", rule.GetProperty("id").GetString());
            Assert.True(rule.GetProperty("enabled").GetBoolean());
            Assert.Equal("high", rule.GetProperty("severity").GetString());

            using var file = await client.GetAsync("/api/repos/analyzed/file?path=src/Reader.cs", TestContext.Current.CancellationToken);
            var fileJson = await file.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            var analyzers = fileJson.GetProperty("analyzers");
            Assert.Equal("CA2016", Assert.Single(analyzers.GetProperty("findings").EnumerateArray())
                .GetProperty("finding").GetProperty("ruleId").GetString());
            var sensor = Assert.Single(analyzers.GetProperty("sensors").EnumerateArray());
            Assert.Equal("sarif", sensor.GetProperty("sensorId").GetString());
            Assert.Equal(1, sensor.GetProperty("suppressedFindings").GetInt32());
        }
        finally
        {
            TemporaryDirectory.Delete(analyzedRoot);
        }
    }
}
