# Quality Studio Analysis Core

`AgentOrchestrator.CodeQuality` runs Quality Studio analyses in process. It is intended for Agent Studio pipeline steps, the `quality` CLI, and CI jobs that already have a repository checkout. It does not host HTTP endpoints or the Quality Studio UI.

## Install

```xml
<PackageReference Include="AgentOrchestrator.CodeQuality" Version="0.1.0" />
```

The package currently targets .NET 10.

## Run named analyses

```csharp
using AgentOrchestrator.CodeQuality;

var core = new QualityAnalysisCore();
var result = await core.RunAsync(new QualityAnalysisRequest(
    RepositoryPath: "/work/repository",
    Analyses:
    [
        QualityAnalysisNames.Boundaries,
        QualityAnalysisNames.Dependencies,
    ],
    Configuration: new QualityAnalysisConfiguration(
        RepositoryId: "service-catalog",
        PersistEvidence: false,
        RuleLibraryPath: ".quality/rules")));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.RuleId} {finding.Title}");
}
```

`QualityAnalysisResult.Findings` uses `QualityFindingEnvelope`, the same versioned finding contract consumed by Quality Studio. `Executions` records availability and provenance for every requested analysis.

## Configuration and rule content

The stable boundary accepts a repository path, a list of analysis names, and `QualityAnalysisConfiguration`. Analysis-specific settings remain string dictionaries so rule-library releases can add content without changing the runner API. `RuleLibraryPath` points to rule content consumed by analyses; the core owns interpretation and callers do not compile or translate rules.

Built-in analysis names are `boundaries`, `dependencies`, and `gitleaks`. Repository evidence is not persisted unless `PersistEvidence` is enabled.

The package contains the runners and sensors, the Quality Studio finding types and JSON contract, embedded prompts and catalog snapshots used by those runners, and the finding JSON schema. It deliberately contains no ASP.NET Core hosting, UI assets, API routes, repository registry, or Agent Studio HTTP client.
