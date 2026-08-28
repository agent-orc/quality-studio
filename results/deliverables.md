# QS-59 deliverables

- Primary dossier: `docs/operations/performance/index.html`
- Workbench metadata: `docs/operations/performance/workbench.json`
- Measurement harnesses (source evidence for the numbers in the dossier): `scripts/perf/backend-perf.mjs`, `scripts/perf/tree-payload-perf.mjs`
- Raw evidence behind every number in the dossier: `docs/operations/performance/results/*.json` (`perf-environment.json`, `perf-api-startup.json`, `perf-frontend-startup.json`, `perf-repository-switch.json`, `perf-tree-payload.json`, `perf-prewarm-events.json`, `perf-edit-churn.json`, `perf-sensor-invalidation.json`, `perf-review-path.json`, `perf-review-scaling.json`, `perf-memory-session.json`)
- Collected mirror for the reviewer: `/home/agent/runner-work/tasks/QS-59/results/index.html`, `/home/agent/runner-work/tasks/QS-59/results/workbench.json`, `/home/agent/runner-work/tasks/QS-59/results/status.md`, `/home/agent/runner-work/tasks/QS-59/results/deliverables.md`
- Build gate output (this redelivery): `/home/agent/runner-work/tasks/QS-59/results/dotnet-build-release.log`

The dossier is decision-ready and recommends Option C (send less on `/api/tree`) sequenced as slices P-1 through P-5, with P-4 (re-warm the snapshot on invalidation) as the other must-approve slice.
