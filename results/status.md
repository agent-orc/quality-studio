# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree equal to `origin/main` at `59941d8`; the latest QS-59 salvage change was replayed without conflict onto that base. Every reported measurement was rerun; no product code changed.

QS-54 is verified as delivered on fresh product base `59941d8`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 5,191-file startup is live at 1.917 s median but not usable until 25.531 s median because the first project request waits 18.50-25.07 s behind cold prewarming.
- The warm root tree is 36,339,195 bytes and project-plus-tree takes 747.76 ms median / 1,081.73 ms p95, missing the 500 ms p95 contract.
- One live review took 21.182 s; the model call was 20.867 s (98.51%). Strict parsing was 0.1081 ms median.
- Review terminal state took 1,690.64 ms median to become visible because of polling; terminal-response-to-visible was 273.19 ms in the current refresh path.
- Anonymous RSS retained 11,936 KiB (+19.75%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. The task's indexed canonical model-routing policy was consulted; no model/thinking override is proposed because the one live sample does not establish that a cheaper route clears the same correctness floor.

Verification:

- Release solution build: passed with zero warnings/errors.
- Routine core tests: 155 passed; one opt-in live integration test skipped. The machine-bound 5,000-file hierarchy test passed.
- Routine API tests: 47 passed. The machine-bound 5,000-file projection test failed at 178 ms against its single-sample 150 ms timing assertion.
- Angular unit tests: 39 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 60.6 ms, tree 8.9-20.5 ms, file 42.8 ms, aspect switch 17.5 ms; all budgets passed.
- Real-backend switch harness: transitions 14.7-24.5 ms and usable state 35.1-154.3 ms across three switches; all fixture budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 478.30 kB against the 480 kB error ceiling, with only 1.70 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
