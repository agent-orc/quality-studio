# Agent pipeline-step prototype

Proof that the `AgentOrchestrator.CodeQuality` analysis core is useful as an in-process **package**, not just as a same-solution `ProjectReference`. It stands in for an Agent Studio pipeline step (AGT-2655): a caller in a different repository that wants Quality Studio findings without going through `QualityStudio.Api` over HTTP.

This project is intentionally **not** part of `QualityStudio.slnx` - it is a standalone consumer, the same way an external repository would be.

## Run it

```bash
# 1. Pack the analysis core into a local folder feed (repo root, gitignored).
./scripts/pack-analysis-core-local-feed.sh

# 2. Restore and run the prototype against any repo path (defaults to ".").
dotnet run --project spikes/agent-pipeline-step -- /path/to/repo results/agent-pipeline-step-report.json
```

`nuget.config` in this folder points `dotnet restore` at `../../.nuget-local-feed` in addition to nuget.org, so `dotnet restore` resolves `AgentOrchestrator.CodeQuality` from the packed `.nupkg`, not from source.

## What it proves

- The package restores and builds from a `.nupkg` with no code from this repository visible to the compiler except through the public API of the packed assembly.
- `QualityReportBuilder().BuildAsync(...)` - the stable "run a named analysis over a repo path + config, get findings back" surface - runs to completion in a process that never opens a socket to `QualityStudio.Api`.
- The result is the same `QualityFinding` shape the API and UI already render, so a pipeline step consuming it in-process gets the identical finding model the operator directive asks for.

What it does **not** prove: publishing to a real NuGet feed, or cross-repository version pinning. Those are extraction-adjacent concerns tracked in `docs/operations/analysis-core-package/index.html`, not part of this slice.
