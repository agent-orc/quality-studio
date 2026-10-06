using QualityStudio.Testing;

namespace AgentOrchestrator.CodeQuality.Tests;

/// <summary>
/// Runs the built-in Roslyn profile against a real two-project solution with the real .NET SDK.
/// Before QS-113 the profile's <c>-p:ErrorLog=…,version=2.1</c> was split at the comma into a SARIF
/// 1.0 log the importer refused, every project wrote the same file, and a second, incremental scan
/// compiled nothing and so logged nothing.
/// </summary>
[Trait("Category", "ToolBound")]
public sealed class RoslynProfileToolTests
{
    [Fact]
    public async Task A_solution_scan_reports_every_project_twice_in_a_row_and_honours_pragma_suppressions()
    {
        var root = Directory.CreateTempSubdirectory("quality-studio-roslyn-").FullName;
        try
        {
            WriteProject(root, "Alpha", """
                namespace Alpha;
                public class Calculator
                {
                    public int Answer() { return 42; }
                }
                """);
            WriteProject(root, "Beta", """
                namespace Beta;
                public class Printer
                {
                #pragma warning disable CA1822
                    public int Quiet() { return 1; }
                #pragma warning restore CA1822
                    public int Loud() { return 2; }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Tool.slnx"), """
                <Solution>
                  <Project Path="Alpha/Alpha.csproj" />
                  <Project Path="Beta/Beta.csproj" />
                </Solution>
                """);
            var sensor = new RoslynAnalyzerSensor();
            var request = new SensorScanRequest(root, Configuration: new Dictionary<string, string>
            {
                [AnalyzerSensorConfiguration.ProfileKey] = "roslyn-build-sarif",
            });

            var first = await sensor.RunAsync(request, TestContext.Current.CancellationToken);
            var second = await sensor.RunAsync(request, TestContext.Current.CancellationToken);

            foreach (var result in new[] { first, second })
            {
                Assert.True(result.Available, result.UnavailableReason);
                Assert.Equal(
                    ["Alpha/Alpha.cs", "Beta/Beta.cs"],
                    result.Findings.Where(finding => finding.RuleId == "CA1822")
                        .Select(finding => finding.Locations[0].Path).Order(StringComparer.Ordinal));
                Assert.True(result.SuppressedFindings >= 1);
            }
            var logs = Directory.GetFiles(Path.Combine(root, ".quality", "preflight", "roslyn"), "*.sarif");
            Assert.Equal(2, logs.Length);
            Assert.All(logs, log => Assert.Contains("\"version\": \"2.1.0\"", File.ReadAllText(log), StringComparison.Ordinal));
        }
        finally
        {
            TemporaryDirectory.Delete(root);
        }
    }

    private static void WriteProject(string root, string name, string source)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AnalysisMode>All</AnalysisMode>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name + ".cs"), source);
    }
}
