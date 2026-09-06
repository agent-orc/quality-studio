# Quality Studio analysis core

`AgentOrchestrator.CodeQuality` runs Quality Studio analyses directly inside a
.NET process. It is intended for Agent Studio pipeline steps, `quality-cli`,
and CI jobs that already have a repository checkout. It does not start a web
server and does not require the Quality Studio UI.

The package id, the assembly name and the root namespace are the same name.
The first stable entry point is `AgentOrchestrator.CodeQuality.QualityAnalysisRunner`.
Call it with a repository path and one or more named analysis definitions. The
result contains execution provenance and findings in the Quality Studio
`QualityFindingEnvelope` model.

```xml
<PackageReference Include="AgentOrchestrator.CodeQuality" Version="0.1.0" />
```

```csharp
using AgentOrchestrator.CodeQuality;

var runner = new QualityAnalysisRunner();
var result = await runner.RunAsync(new QualityAnalysisRequest(
    "/work/repository",
    [new QualityAnalysisDefinition(QualityAnalysisNames.Boundaries)],
    RepositoryId: "payments"));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.Title}");
}
```

Built-in analysis names are exposed by `QualityAnalysisNames`. Configuration
is passed per analysis as string key/value data, matching the underlying sensor
contract. Repository metadata is not written unless `PersistMetadata` is set.
When enabled, metadata is written through `QualityDataRoot` to the external
project data directory, never beneath the analyzed checkout.

Rule libraries are content supplied by the host and translated into analysis
configuration or registered analysis implementations. They are deliberately
not coupled to the package's release lifecycle. HTTP endpoints, UI hosting,
repository registration, and Agent Studio transport clients remain outside
this package.

The caller owns checkout lifecycle, path authorization, process isolation,
logging, and any persistence or later upload of results. Some named analyses
invoke repository-configured tools; Gitleaks can resolve its pinned binary when
the caller does not supply one, or the caller points `QUALITY_GITLEAKS_PATH` at
a binary of its own for offline hosts.

The package targets .NET 10. CI packs it on every push to `main`
(`dotnet pack`, artifact `nuget-package`); publication to a NuGet feed is a
separate, not yet configured release step.
