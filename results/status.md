# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree already equal to `origin/main` at `d7404cd`, making the active integration rebase a verified no-op. Existing QS-59 harness artifacts were restored on that clean base and every reported measurement was rerun; no product code changed.

QS-54 is verified as delivered on fresh product base `d7404cd`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 518.53 ms median but not usable until 6.140 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 247.64 ms median / 761.68 ms p95, missing the 500 ms p95 contract.
- One live review took 16.109 s; the model call was 15.734 s (97.67%). Strict parsing was 0.1709 ms median.
- Review terminal state took 1,732.44 ms median to become visible because of polling; terminal-response-to-visible was 322.78 ms in the current refresh path.
- Anonymous RSS retained 14,088 KiB (+23.29%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Routine core tests: 151 passed; one opt-in live integration test skipped. The separate machine-bound 5,000-file hierarchy test passed.
- Routine API tests: 44 passed. The separate machine-bound 5,000-file projection test passed.
- Angular unit tests: 37 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 67.5 ms, tree 13.1–18.3 ms, file 41.9 ms, aspect switch 16.3 ms; all budgets passed.
- Real-backend switch harness: transitions 15.3–25.4 ms and usable state 29.7–197.8 ms; all three switch budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 475.56 kB against the 480 kB error ceiling, with only 4.44 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
