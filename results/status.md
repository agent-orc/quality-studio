# QS-59 status

Status: decision pending
Phase: decision ready
Primary deliverable: `docs/operations/performance/index.html`

## Base reconciliation

The dossier's harness runs were measured on 2026-08-24 against QS commit `e31089c`. This delivery rebases that dossier onto the current `origin/main` tip `da620fa` (the working branch already matched `origin/main` exactly before this change, so no local/remote drift and no merge conflict existed).

`git diff e31089c da620fa --stat` touches 18 files: a new `DotNetBuildSensor` and its DI registration/default-sensor wiring in `RepositoryRegistry.cs` and `Program.cs`, `frontend/eslint.config.mjs` and lockfile updates, two unrelated `docs/operations/*` dossiers, and their test files. None of the eight files the dossier's findings depend on changed:
`RepositorySnapshotPrewarmer.cs`, `RepositoryHierarchyCache.cs`, `ProjectDashboard.cs`, `ApiContracts.cs`, `quality-api.ts`, `app.ts`, the request-handling parts of `Program.cs`, and `project-switch-perf.mjs`. The measured numbers therefore still describe the current code; only the dossier's meta line and a new "Redelivery note" box were added to record this reconciliation.

## Measured conclusions (unchanged, from the 2026-08-24 pass)

- QS-54's background prewarm is delivered and holds: startup is unaffected by registry size, and warm `/api/project` is 31.87 ms on the 5,116-file repository.
- The switch contract still breaks: `/api/tree` is the uninstrumented other half of the same critical path, at 659.10 ms of a 664.89 ms warm path, 33.87 MiB, 15,961 file nodes for 2,596 distinct paths (6.1x amplification).
- Any write to the repository invalidates the snapshot and the next request rebuilds it inline: an 81.95 ms sensor scan cost the following dashboard request 16,143.72 ms.
- Review preflight renders prompts synchronously in the request at ~164 ms per subject file: 198,976.86 ms for a 1,215-file module.
- Memory is stable over 640 requests (313.20 to 336.54 MiB, no monotonic trend); the unbounded structures are recorded but did not dominate at measured scale.
- The requested model-versus-parsing split is unavailable: the review path emits no phase telemetry at all.

Recommendation: approve Option C (send less on `/api/tree` — conditional requests, dedupe, bounded subtrees) sequenced P-1 to P-3, plus P-4 (re-warm the snapshot on invalidation) and P-5 (give the review path the phase telemetry the switch path already has). Full detail and exit criteria are in dossier section 10.

## Verification this run (the delivery gate)

- `dotnet build QualityStudio.slnx -c Release` at `da620fa` with this dossier applied: build succeeded, 0 warnings, 0 errors (~15 s). Log mirrored to `/home/agent/runner-work/tasks/QS-59/results/dotnet-build-release.log`.
- Dossier `<style>` block diffed byte-for-byte against the style reference `agent-taskboard`'s `docs/operations/haertung-verteilte-ausfuehrung/index.html`: identical after copying the missing SVG-role rules (`.s-node`, `.s-node2`, `.s-tiny`, `.s-flow`, `.s-commit`, `.building-block figure/figcaption`, `.evidence-link`) into the dossier.
- `docs/operations/performance/workbench.json` checked against spec: `schemaVersion: 1`, `status: "decision-pending"`, `phase: "decision-ready"`, `sourceTaskKeys: ["QS-59"]` — matches, unchanged.
- The live/benchmark measurement harnesses (`scripts/perf/backend-perf.mjs`, `scripts/perf/tree-payload-perf.mjs`) were not re-executed this run — the base reconciliation above shows none of their measured code paths changed since the 2026-08-24 pass, and the orchestrator's final-round steer scoped this delivery to rebase-and-redeliver, no feature rework.
