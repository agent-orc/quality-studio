# Quality Studio Analysis Core

`QualityStudio.Analysis.Core` runs Quality Studio analyses in-process. It is the
library boundary for Agent Studio pipeline steps, `quality-cli`, and CI hosts
that already have a repository checkout and do not need the Quality Studio HTTP
API or UI.

The package currently keeps the established
`AgentOrchestrator.CodeQuality` namespace and assembly name so existing callers
remain source- and binary-compatible.

## Install

```xml
<PackageReference Include="QualityStudio.Analysis.Core" Version="0.1.0" />
```

## Run named analyses

```csharp
using AgentOrchestrator.CodeQuality;

var core = QualityAnalysisCore.CreateDefault();
var result = await core.RunAsync(new QualityAnalysisRequest(
    RepositoryPath: checkoutPath,
    Analyses:
    [
        new NamedAnalysis("boundaries"),
        new NamedAnalysis("dependencies", new Dictionary<string, string>
        {
            ["ecosystems"] = "dotnet",
        }),
    ]));

foreach (var finding in result.Findings)
{
    Console.WriteLine($"{finding.Severity}: {finding.RuleId} {finding.Title}");
}
```

`RunAsync` accepts a repository path plus one or more named analyses. Each
analysis receives an opaque string configuration dictionary owned by that
analysis. The result uses Quality Studio's `ReviewFinding` model and retains a
per-analysis availability and provenance record. An unavailable analyzer is an
explicit result; it is never reported as a pass.

Built-in names are `boundaries`, `coverage`, `dependencies`, `eslint`,
`gitleaks`, `roslyn`, `sarif`, and `tsc`. Several analyzers require configuration
such as a command or report path. Use `ListAnalyses()` to discover the package's
available names and supported scopes.

## Rule-library boundary

Rules are content, not package code. A rule-backed analysis receives its rule
library location or selection through `NamedAnalysis.Configuration`; the rule
library remains independently versioned repository/package content. The core
does not depend on the Quality Studio API, UI, database, or a rule-authoring
host.

## Hosting and safety

The caller owns repository checkout lifecycle, path authorization, process
isolation, cancellation, logging, and any persistence or upload of results. Set
`PersistMetadata` to `false` when the host wants a read-only analysis run. The
package may invoke configured local tools; the Gitleaks analyzer can resolve its
pinned binary when no local executable is configured.
