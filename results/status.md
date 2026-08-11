# QS-59 status

Status: decision pending
Phase: decision-ready
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

Base reconciliation: the configured origin has no `develop` ref and advertises `origin/main` as its default. A fresh fetch showed the worktree already equal to `origin/main` at `d7404cd`, making the integration rebase a verified no-op. The four prior QS-59 dossier/harness commits were replayed cleanly; newer product and operation-dossier work was preserved.

QS-54 is verified as delivered on fresh product base `d7404cd`: `git cherry` marks successful result commit `26fd785` as patch-equivalent, and 10 of its 18 files are byte-identical. Direct inspection, the collected hash ledger, and live probes confirm that the eight later-evolved shell/API/style files retain the hierarchy cache, prewarmer, dashboard cache, stale-while-revalidate UI, phase telemetry, tests, and browser harness.

Measured conclusions:

- Real 3,927-file startup is live at 817.54 ms median but not usable until 7.493 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 277.47 ms median / 825.79 ms p95, missing the 500 ms p95 contract.
- One live review took 16.760 s; the model call was 16.289 s (97.19%). Strict parsing was 0.1036 ms median.
- Review terminal state took 1,687.86 ms median to become visible because of polling; terminal-response-to-visible was 271.87 ms in the current refresh path.
- Anonymous RSS retained 10,380 KiB (+17.83%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification:

- Release solution build: passed with zero warnings/errors.
- Core tests: 152 passed; one opt-in live integration test skipped.
- API tests: 44 passed and one machine-bound 5,000-file timing case failed at 275 ms against 150 ms in the full suite; that case passed when rerun alone.
- Angular unit tests: 37 passed using the cached Chromium launcher with the repository-supported no-sandbox mode.
- Browser interaction harness: dashboard 78.5 ms, tree 20.9–21.4 ms, file 53.1 ms, aspect switch 10.7 ms; all budgets passed.
- Real-backend switch harness: transitions 8.0–27.2 ms and usable state 46.5–193.9 ms; all three switch budgets passed.
- Backend, parser, rendering, and live-review probes: completed and written to the collected result directory.
- Production Angular build: passed at 475.56 kB against the 480 kB error ceiling, with only 4.44 kB headroom and existing compiler/CSS/CommonJS warnings; the dossier recommends code splitting without raising the budget.
- Requested Agent Taskboard document style: copied exactly; the extracted `<style>` blocks have the same SHA-256 (`4e1f500e11d2…`).
