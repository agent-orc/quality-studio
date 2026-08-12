# Quality Studio Analysis Core

`QualityStudio.Analysis` runs Quality Studio repository analyses directly inside a .NET process. It does not call the Quality Studio HTTP API and does not require the Studio UI.

## Install

```xml
<PackageReference Include="QualityStudio.Analysis" Version="0.1.0" />
```

The first package targets .NET 10.

## Run analyses

```csharp
using QualityStudio.Analysis;

var runner = new AnalysisRunner();
var result = await runner.RunAsync(new AnalysisRunRequest(
    RepositoryPath: repositoryPath,
    Analyses:
    [
        new NamedAnalysis("boundaries"),
        new NamedAnalysis("dependencies", new Dictionary<string, string>
        {
            ["ecosystems"] = "dotnet,npm",
        }),
    ]));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.Title}");
}
```

`AnalysisRunResult.Findings` uses the canonical Quality Studio `ReviewFinding` model. Each named result also carries availability and provenance. Metadata writes are off by default; set `PersistMetadata` only when the caller owns those repository artifacts.

## Built-in analysis names

- `boundaries`
- `coverage`
- `dependencies`
- `eslint`
- `gitleaks`
- `roslyn`
- `sarif`
- `tsc`

Some analyses require tools or per-analysis configuration. An unavailable tool is reported in its named result when the sensor can do so safely.

## Rule-library boundary

Rule libraries are content providers, not hosts. A caller can construct `AnalysisRunner` with its own `IReviewSensor` implementations and pass rule-specific values through `NamedAnalysis.Configuration`. This keeps rule content independently testable and prevents UI, HTTP DTOs, or repository-registration concerns from entering the analysis contract.

## Intended standalone consumers

- Agent Studio pipeline steps, including AGT-2655, load the package and analyze the checked-out task repository in process.
- `quality-cli` provides a local operator and automation surface over the same runner.
- CI jobs can run selected analyses, inspect canonical findings, and apply their own gates or serializers.

The package includes the runner contract, built-in sensors, canonical finding types, provenance types, embedded review prompts/catalogues, and the sensor extension seam. It deliberately excludes ASP.NET hosting, Quality Studio API endpoints, UI assets, repository registration, and Agent Studio HTTP handover.
