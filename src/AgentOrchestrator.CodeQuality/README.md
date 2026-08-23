# QualityStudio.AnalysisCore

Quality Studio's deterministic analysis core as an in-process library: run named analyses over a
repository path and get back findings, with no ASP.NET Core, no HTTP hosting, and no dependency
on QualityStudio.Api.

This is the same analysis engine QualityStudio.Api hosts over HTTP for the Quality Studio UI. The
package lets a caller embed it directly — as a DLL, in-process — instead of talking to that API.

## Install

```
dotnet add package QualityStudio.AnalysisCore
```

## Quick start

```csharp
using AgentOrchestrator.CodeQuality;

var core = AnalysisCore.CreateDefault();

// One named analysis over a repository path.
var gitleaks = await core.RunAsync("gitleaks", repositoryRoot: "/path/to/repo");

// Every registered analysis, findings keyed by analysis id.
var all = await core.RunAsync(repositoryRoot: "/path/to/repo");
foreach (var (analysisId, result) in all)
{
    Console.WriteLine($"{analysisId}: {(result.Available ? result.Findings.Count : 0)} finding(s)");
}
```

`core.AnalysisIds` lists what is registered. The built-in default set:

| Analysis id    | What it checks                                  |
|----------------|--------------------------------------------------|
| `gitleaks`     | Committed secrets, via the gitleaks scanner       |
| `dependencies` | Known-vulnerable dependency versions              |
| `boundaries`   | Inbound/outbound boundary inventory for a module  |
| `coverage`     | Test coverage ingestion from existing reports      |
| `roslyn`       | .NET/Roslyn analyzer diagnostics (SARIF)          |
| `eslint`       | ESLint diagnostics (SARIF)                        |
| `tsc`          | TypeScript compiler diagnostics                    |

Each analysis probes its own tool availability (git, gitleaks, dotnet, npx/eslint, tsc) at run
time and reports itself unavailable rather than throwing when a required tool is missing on the
host. `SensorScanResult.Findings` is Quality Studio's finding model (`ReviewFinding`) — the same
shape QualityStudio.Api serializes to review-meta documents and the QS UI.

## What this package deliberately does not include

- No ASP.NET Core, no HTTP server, no controllers.
- No outbound calls to Agent Studio's task API (creating/handing over task cards) — that is an
  orchestration concern that lives in QualityStudio.Api, not in the analysis core.
- No UI. Findings produced in-process here reach the Quality Studio UI later, through whatever
  process persists and syncs them there — not live.

Rule content (the QS rule library) is consumed as data — JSON loaded from a configured path — not
as a code dependency, so adding or changing rules never requires a new release of this package.

## Who this is for

- A pipeline step that wants findings without standing up QualityStudio.Api (see
  `docs/operations/analysis-core-package/` in the Quality Studio repository for the current
  standalone consumers and the extraction decision).
- `quality-cli`, run locally or in CI, for the same reason.
- Anything else that has a repository checkout on disk and wants Quality Studio's deterministic
  findings without a network hop.
