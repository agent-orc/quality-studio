# QS-59 status

Status: decision pending  
Phase: decision-ready  
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. The worktree was fetched/rebased to fresh `origin/main` at `06e88c3`, then only the QS-59 salvage delta was replayed; newer operation dossiers were preserved.

QS-54 is verified as delivered on fresh source base `06e88c3`: 17 of 18 files from successful result commit `26fd785` are byte-identical; the eighteenth retains the QS-54 behavior and only adds later design-token/finding-detail styles. The prewarmer, caches, stale-while-revalidate UI, telemetry, tests, and harness are present.

Measured conclusions:

- Real 3,927-file startup is live at 502 ms median but not usable until 6.828 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 239.93 ms median / 708.06 ms p95, missing the 500 ms p95 contract.
- One live review took 18.800 s; the model call was 18.443 s (98.1%). Strict parsing was 0.1102 ms median.
- Review terminal state took 1,413.19 ms median to become visible because of polling; response-to-paint was 10.24 ms.
- Anonymous RSS retained 7,040 KiB (+12.06%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 135 passed; one opt-in live integration test skipped.
- API tests: 41 passed; the concurrent machine-bound 5,000-file timing case failed at 274 ms against 150 ms, then passed when rerun in isolation.
- Angular unit tests: 17 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: project 22.9 ms, tree 4.6–20.9 ms, file 41.7 ms, aspect switch 17.7 ms; all budgets passed.
- Real-backend switch harness: all three transition and usable-state budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: failed the pre-existing initial-bundle gate at 498.62 kB against 480 kB; the dossier recommends code splitting without raising the budget.
