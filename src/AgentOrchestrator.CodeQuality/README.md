# AgentOrchestrator.CodeQuality

`AgentOrchestrator.CodeQuality` is Quality Studio's in-process analysis package. It lets a server-side host run named analyses against a repository path and receive Quality Studio findings without starting the Quality Studio HTTP API or UI.

The first stable host surface is `IQualityAnalysisRunner`. `QualityAnalysisRunner.CreateDefault()` includes the built-in boundary, coverage, dependency, ESLint, Gitleaks, Roslyn, SARIF, and TypeScript analyses. A host may instead construct the runner with its own `IReviewSensor` implementations.

```csharp
using AgentOrchestrator.CodeQuality;

var runner = QualityAnalysisRunner.CreateDefault();
var result = await runner.RunAsync(new QualityAnalysisRequest(
    repositoryPath,
    [new QualityAnalysisSelection("boundaries")],
    PersistMetadata: false));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.RuleId} - {finding.Title}");
}
```

Each analysis receives its own string configuration dictionary. For example, coverage accepts `reportPaths`; SARIF-based analyses accept `reportPath`, `workingDirectory`, and an optional `command`. Unknown analysis names and invalid scopes fail explicitly. A tool that cannot run returns an unavailable execution rather than silently reporting a pass.

## Boundaries

The package contains analysis orchestration, the Quality Studio finding model, sensors, review primitives, and embedded prompts/catalogues needed by those implementations. It does not contain ASP.NET hosting, API contracts, UI code, or Agent Studio HTTP clients.

The Quality Studio rule library is content supplied to the core, not a dependency direction back into its publisher or UI. Repository/global review inputs remain external content resolved through the package's input contracts; a future rule-library adapter can implement a sensor or materialize those inputs without the core referencing the rule-library host.

The package does not commit, push, merge, or manage task worktrees. Callers own repository lifecycle and decide whether analysis metadata should be persisted.
