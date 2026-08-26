# Quality Studio Analysis Core

`AgentOrchestrator.CodeQuality` is the host-independent analysis library behind Quality Studio. It runs named analyses in-process over a repository path and returns the Quality Studio `ReviewFinding` model. It does not require ASP.NET Core, the Quality Studio API, or the browser UI.

## Install

```shell
dotnet add package AgentOrchestrator.CodeQuality
```

The first publishable version targets .NET 10.

## Run analyses in-process

```csharp
using AgentOrchestrator.CodeQuality;

var runner = new AnalysisRunner();
var result = await runner.RunAsync(new AnalysisRequest(
    RepositoryPath: repositoryPath,
    Analyses:
    [
        new AnalysisConfiguration("boundaries"),
        new AnalysisConfiguration("sarif", new Dictionary<string, string>
        {
            ["reportPath"] = ".quality/analyzers/results.sarif",
        }),
    ]));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.RuleId} — {finding.Title}");
}
```

`AnalysisRunner.ListAnalyses()` reports the registered names without probing tools. A named result distinguishes an unavailable analyzer from an available analyzer that found nothing. Runs through this façade do not persist review metadata.

## Configuration and rule content

Each `AnalysisConfiguration` carries caller-owned string settings to the selected analysis. The package supplies execution, normalization, provenance, and finding contracts. Rule libraries and repository-specific policies are content dependencies: keep them separately versioned and register their sensor implementations through `AnalysisRunner(IEnumerable<IReviewSensor>)` instead of coupling rule content to the web host.

## Intended consumers

- Agent Studio pipeline steps, including AGT-2655, invoke the DLL directly against the checked-out task repository.
- `quality-cli` provides local and scripted access to the same façade.
- CI jobs use the package without starting Quality Studio API or UI processes.

The ASP.NET Core API remains an adapter for remote/UI clients; it is not required for analysis.
