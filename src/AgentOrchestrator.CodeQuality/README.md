# AgentOrchestrator.CodeQuality

The in-process analysis core of [Quality Studio](https://github.com/agent-orc/quality-studio). It has no ASP.NET, no hosting, and no HTTP-server dependencies - it is a plain .NET class library meant to run **inside a caller's own process**: a CLI, a CI step, or another product's pipeline.

## What it does

- Builds the repository → module → namespace → file → function hierarchy and reads review-meta files that live next to the reviewed code.
- Evaluates staleness (`fresh` / `stale` / `missing`) against the current content hash.
- Runs deterministic sensors: `gitleaks` secret scanning, dependency vulnerability scanning, coverage, boundary/HTTP-surface inventory, and SARIF ingestion (Roslyn, ESLint).
- Aggregates sensor and review output into the shared `QualityFinding` model, scorecards, and trend series.
- Drives agent-backed reviews (code / security / performance) through the `CodingAgentRunner` package when a caller wants agent findings in addition to deterministic ones.

## What it does not do

- It does not host an HTTP API. `QualityStudio.Api` is a separate project that hosts this library behind endpoints for the Quality Studio UI.
- It does not manage git worktrees, commit, or push. Callers that need change-aware review (`ChangeDiffCommand`, `GitMergeRangeChangeSetProvider`) pass in a repository path they already control.
- It does not decide *when* to run. Scheduling, queuing, and orchestration belong to the caller.

## Install

```
dotnet add package AgentOrchestrator.CodeQuality
```

## Quick start: run a named analysis over a repo path

The stable entry point for "run an analysis over a repo path + config, get findings back" is `QualityReportBuilder`:

```csharp
using AgentOrchestrator.CodeQuality;

var sensors = new QualityReportSensor[]
{
    new("gitleaks", GitleaksBinaryResolver.PinnedVersion, Enabled: true),
    new("dependencies", DependencyVulnerabilitySensor.SensorVersion, Enabled: true),
};

var report = await new QualityReportBuilder().BuildAsync(
[
    new QualityReportRepository("my-repo", "My Repo", root: "/path/to/repo", Sensors: sensors),
]);

foreach (var finding in report.Repositories[0].Findings)
{
    Console.WriteLine($"{finding.Severity} {finding.RuleId} {finding.Locations[0].Path}");
}
```

`report.Repositories[0].Findings` is a flat `IReadOnlyList<QualityFinding>` - the same finding shape the Quality Studio API and UI render. Nothing here opens a socket or expects a running server.

For a narrower slice - "just the deterministic sensors, no agent calls, no scorecard" - use `SensorRegistry` and `DeterministicEvidenceCollector` directly; see `QualityReportBuilder`'s own implementation for the composition pattern.

## Who consumes this today

- `quality` and `quality-cli` (this repository, `src/quality*`) - the command-line tools, in-process.
- `QualityStudio.Api` (this repository) - hosts the same library behind HTTP for the browser UI.
- Prototype: `spikes/agent-pipeline-step` in this repository packs and consumes this library from a local NuGet feed the same way an external pipeline step would, without any HTTP round trip.

See `docs/operations/analysis-core-package/index.html` in this repository for the fuller split rationale, the standalone-usefulness answer, and the (not-yet-taken) repository-extraction decision.

## Versioning

Pre-1.0: the public surface may still move. Track the `AgentOrchestrator.CodeQuality` namespace types you use; anything under `internal` is never part of the contract.

## License

Apache-2.0. See the repository root `LICENSE`.
