# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree equal to `origin/main` at `8fe2552`; the latest QS-59 task change was then replayed cleanly onto that base. Every reported measurement was rerun; no product code changed.

QS-54 is verified as delivered on fresh product base `8fe2552`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 637.63 ms median but not usable until 7.493 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 299.61 ms median / 676.80 ms p95, missing the 500 ms p95 contract.
- One live review took 20.468 s; the model call was 20.161 s (98.50%). Strict parsing was 0.1173 ms median.
- Review terminal state took 1,688.49 ms median to become visible because of polling; terminal-response-to-visible was 272.70 ms in the current refresh path.
- Anonymous RSS retained 14,568 KiB (+24.75%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Routine core tests: 156 passed; one opt-in live integration test skipped. The machine-bound 5,000-file hierarchy test passed.
- Routine API tests: 47 passed. The machine-bound 5,000-file projection test failed at 216 ms against its single-sample 150 ms timing assertion under the measured host load.
- Angular unit tests: 39 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 71.4 ms, tree 14.0–25.1 ms, file 48.2 ms, aspect switch 14.5 ms; all budgets passed.
- Real-backend switch harness: transitions 15.6–33.2 ms across two fresh-fixture runs; usable state was 30.7–566.5 ms. Five of six switches passed, one missed the 500 ms contract, and the complete rerun passed at 30.7–208.4 ms.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 478.30 kB against the 480 kB error ceiling, with only 1.70 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
