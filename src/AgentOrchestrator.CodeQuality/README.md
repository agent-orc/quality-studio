# AgentOrchestrator.CodeQuality

`AgentOrchestrator.CodeQuality` is Quality Studio's host-neutral .NET analysis library. It runs inside the caller's process: no Quality Studio server or HTTP request is required.

The package contains the Quality Studio review and finding contracts, repository hierarchy and staleness services, deterministic analyzers, analysis orchestration, and agent-review extension points. It does not contain ASP.NET hosting, Quality Studio UI code, repository registration, background job hosting, or Agent Studio HTTP integration.

## Run named analyses

```csharp
using AgentOrchestrator.CodeQuality;

var runner = new AnalysisRunner();
var result = await runner.RunAsync(new AnalysisRequest(
    "/work/my-repository",
    [AnalysisNames.Boundaries, AnalysisNames.Dependencies]));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.RuleId} at {finding.Locations[0].Path}");
}
```

`AnalysisResult.Findings` uses the same `ReviewFinding` model stored in Quality Studio review metadata and projected by the UI. Each named execution also reports availability and producer provenance. Library runs do not write analyzer artifacts unless `AnalysisConfiguration.PersistArtifacts` is set to `true`.

## Rule-library boundary

Rule libraries are content, not a dependency of the analysis core. Implement `IAnalysisRuleProvider` to resolve a versioned `AnalysisRuleSet` from files or another caller-owned source, then pass it to `AnalysisRunner`. Request settings can override rule-set settings for one run. This keeps rule releases and storage choices outside the core package while preserving rule identity in the result.

## Standalone consumers

- Agent Studio pipeline steps such as AGT-2655 load this package and analyze the already-mounted task repository in process.
- `quality-cli` uses the same programmatic surface for local analysis.
- CI jobs reference the package from a small .NET runner and gate on returned findings or analyzer availability.

These consumers need the package assembly, its NuGet dependencies, any explicitly selected external analyzer tools, access to the repository path, and caller-supplied rule/configuration content. They do not need the Quality Studio API or UI.
