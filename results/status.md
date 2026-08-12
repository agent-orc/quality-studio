# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree already equal to `origin/main` at `59941d8`, making the integration rebase a verified no-op. The five prior QS-59 dossier/harness commits were replayed cleanly; newer product and operation-dossier work was preserved.

QS-54 is verified as delivered on fresh product base `59941d8`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 808.38 ms median but not usable until 10.005 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 423.74 ms median / 954.10 ms p95, missing the 500 ms p95 contract.
- One live review took 18.742 s; the model call was 18.202 s (97.12%). Strict parsing was 0.1594 ms median.
- Review terminal state took 1,725.92 ms median to become visible because of polling; terminal-response-to-visible was 314.29 ms in the current refresh path.
- Anonymous RSS retained 21,800 KiB (+34.24%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 156 passed; one opt-in live integration test skipped.
- API tests: 47 passed and one machine-bound 5,000-file timing case failed at 293 ms against 150 ms while running concurrently with the browser suite; that case passed when rerun alone.
- Angular unit tests: 39 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 71.2 ms, tree 14.1–19.3 ms, file 48.8 ms, aspect switch 17.3 ms; all budgets passed.
- Real-backend switch harness: transitions 7.7–19.4 ms and usable state 30.3–138.5 ms; all three switch budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 478.30 kB against the 480 kB error ceiling, with only 1.70 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
