# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree already equal to `origin/main` at `62ca75f`, making the integration rebase a verified no-op. Only the three prior QS-59 dossier/harness commits were replayed; newer product and operation-dossier work was preserved.

QS-54 is verified as delivered on fresh product base `62ca75f`: 10 of 18 files from successful result commit `26fd785` are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 775.84 ms median but not usable until 7.967 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 494.81 ms median / 828.93 ms p95, missing the 500 ms p95 contract.
- One live review took 18.136 s; the model call was 17.498 s (96.48%). Strict parsing was 0.2160 ms median.
- Review terminal state took 1,692.33 ms median to become visible because of polling; terminal-response-to-visible was 277.63 ms in the current refresh path.
- Anonymous RSS retained 16,020 KiB (+25.99%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 146 passed; one opt-in live integration test skipped.
- API tests: 44 passed; the machine-bound 5,000-file timing case failed at 254 ms against 150 ms in the suite, then passed when rerun alone.
- Angular unit tests: 35 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 116 ms, tree 3.8–35.3 ms, file 118 ms, aspect switch 24.4 ms; all budgets passed.
- Real-backend switch harness: transitions 13.8–28.4 ms and usable state 40.4–202.6 ms; all three switch budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 475.24 kB against the 480 kB error ceiling, with only 4.76 kB headroom; the dossier recommends code splitting without raising the budget.
