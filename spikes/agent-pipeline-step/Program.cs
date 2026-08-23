using System.Diagnostics;
using AgentOrchestrator.CodeQuality;

// Stand-in for an Agent Studio pipeline step (AGT-2655): consume the Quality
// Studio analysis core in-process, as a packed NuGet library, and get the
// shared QualityFinding model back - no HTTP call to QualityStudio.Api.
var repoPath = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
if (!Directory.Exists(repoPath))
{
    Console.Error.WriteLine($"Repository path does not exist: {repoPath}");
    return 2;
}

var outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

var sensors = new QualityReportSensor[]
{
    new("gitleaks", GitleaksBinaryResolver.PinnedVersion, Enabled: true),
    new("dependencies", DependencyVulnerabilitySensor.SensorVersion, Enabled: true),
};

var stopwatch = Stopwatch.StartNew();
var report = await new QualityReportBuilder().BuildAsync(
[
    new QualityReportRepository("pipeline-step-target", new DirectoryInfo(repoPath).Name, repoPath, Sensors: sensors),
]);
stopwatch.Stop();

var repository = report.Repositories[0];
Console.WriteLine(
    $"agent-pipeline-step: analyzed {repository.Name} in-process | score {repository.Scorecard.Score} ({repository.Scorecard.Grade}) | " +
    $"findings {repository.Findings.Count} | {stopwatch.ElapsedMilliseconds} ms | no HTTP call made");

foreach (var finding in repository.Findings)
{
    Console.WriteLine($"  {finding.Severity,-8} {finding.RuleId} {finding.Locations.FirstOrDefault()?.Path}");
}

if (outputPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var rendered = QualityReportRenderer.Render(report, QualityReportFormat.Json);
    await File.WriteAllTextAsync(outputPath, rendered);
    Console.WriteLine($"wrote {outputPath}");
}

return 0;
