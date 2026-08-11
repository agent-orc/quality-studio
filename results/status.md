# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. The worktree was fetched/rebased to fresh `origin/main` at `e9623e6`, then only the two QS-59 salvage commits were replayed; newer operation dossiers were preserved.

QS-54 is verified as delivered on fresh source base `e9623e6`: 11 of 18 files from successful result commit `26fd785` are byte-identical. Direct inspection and live probes confirm that the seven later-evolved shell/API files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 651 ms median but not usable until 8.536 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 301.69 ms median / 696.43 ms p95, missing the 500 ms p95 contract.
- One live review took 14.834 s; the model call was 14.534 s (98.0%). Strict parsing was 0.1075 ms median.
- Review terminal state took 1,411.72 ms median to become visible because of polling; response-to-paint was 8.63 ms.
- Anonymous RSS retained 18,520 KiB (+28.05%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 142 passed; one opt-in live integration test skipped.
- API tests: 42 passed; the machine-bound 5,000-file timing case failed at 302 ms against 150 ms in the suite, then passed when rerun alone.
- Angular unit tests: 20 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: project 68.2 ms, tree 12.3–19.6 ms, file 37.6 ms, aspect switch 16.9 ms; all budgets passed.
- Real-backend switch harness: all three transition and usable-state budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 476.18 kB against the 480 kB error ceiling, with only 3.82 kB headroom; the dossier recommends code splitting without raising the budget.
