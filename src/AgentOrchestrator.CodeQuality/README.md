# Quality Studio Analysis Core

`QualityStudio.Analysis.Core` runs Quality Studio analyses directly inside a
.NET process. It is intended for Agent Studio pipeline steps, `quality-cli`,
and CI jobs that already have a repository checkout. It does not start a web
server and does not require the Quality Studio UI.

The first stable entry point is `QualityStudio.Analysis.QualityAnalysisRunner`.
Call it with a repository path and one or more named analysis definitions. The
result contains execution provenance and findings in the Quality Studio
`QualityFindingEnvelope` model.

```xml
<PackageReference Include="QualityStudio.Analysis.Core" Version="0.1.0" />
```

```csharp
using QualityStudio.Analysis;

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

Rule libraries are content supplied by the host and translated into analysis
configuration or registered analysis implementations. They are deliberately
not coupled to the package's release lifecycle. HTTP endpoints, UI hosting,
repository registration, and Agent Studio transport clients remain outside
this package.

The caller owns checkout lifecycle, path authorization, process isolation,
logging, and any persistence or later upload of results. Some named analyses
invoke repository-configured tools; Gitleaks can resolve its pinned binary when
the caller does not supply one.

The package currently targets .NET 10 and uses the existing
`AgentOrchestrator.CodeQuality` model namespace for compatibility. The stable
orchestration facade lives in `QualityStudio.Analysis`.

