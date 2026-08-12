# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree already equal to `origin/main` at `59941d8`, making the integration rebase a verified no-op. The six prior QS-59 dossier/harness commits were replayed cleanly; newer product and operation-dossier work was preserved.

QS-54 is verified as delivered on fresh product base `59941d8`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 535.53 ms median but not usable until 6.106 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 234.58 ms median / 697.32 ms p95, missing the 500 ms p95 contract.
- One live review took 19.030 s; the model call was 18.698 s (98.26%). Strict parsing was 0.1016 ms median.
- Review terminal state took 1,698.98 ms median to become visible because of polling; terminal-response-to-visible was 282.24 ms in the current refresh path.
- Anonymous RSS retained 20,516 KiB (+34.73%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 156 passed; one opt-in live integration test skipped.
- API tests: 47 passed and the 5,000-file dashboard timing case failed at 186 ms against 150 ms in both the full suite and an isolated rerun.
- Angular unit tests: 39 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 62.3 ms, tree 15.7–20.1 ms, file 44.8 ms, aspect switch 15.5 ms; all budgets passed.
- Real-backend switch harness: transitions 14.6–21.7 ms and usable state 30.2–133.6 ms; all three switch budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 478.30 kB against the 480 kB error ceiling, with only 1.70 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
