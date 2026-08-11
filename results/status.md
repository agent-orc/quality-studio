# QS-59 status

Status: decision pending  
Phase: decision-ready  
Primary deliverable: `/home/agent/runner-work/PROJ-016/worktrees/QS-59/docs/operations/performance/index.html`

QS-54 is verified as delivered: all 18 files from successful result commit `26fd785` are byte-identical on current `main` at `3e0b655`; three branch differences are unrelated later updates.

Measured conclusions:

- Real 3,927-file startup is live at 742 ms median but not usable until 10.225 s median because the first request waits behind cold prewarming.
- The warm root tree is 29,119,333 bytes and project-plus-tree takes 607.82 ms median / 905.12 ms p95, missing the 500 ms contract.
- One live review took 17.892 s; the model call was 17.243 s (96.4%). Strict parsing was 0.1087 ms median.
- Review terminal state took 1,425.64 ms median to become visible because of polling; response-to-paint was 19.71 ms.
- Anonymous RSS retained 15,520 KiB (+27.45%) after 100 Git-state invalidations, consistent with the unbounded dashboard cache.

Recommendation: approve lazy tree transport, persisted verified startup snapshots, push review-run events with phase telemetry, and bounded projection retention. No model/thinking override is proposed because the repository's prompt-named routing-policy document is absent.

Verification: the Release solution build passed with zero warnings/errors; the real-backend browser switch harness passed all switch budgets; the custom probes completed. The production Angular build exposed a pre-existing 494.94 kB initial bundle against its 480 kB error budget. Core tests passed 135 with one opt-in live test skipped; API tests passed 41, while the 5,000-file `MachineBound` timing test failed at 196/258 ms under solution-wide concurrent load and passed when rerun in isolation. The Karma unit bundle compiled, but its stock `ChromeHeadless` launcher could not start because this host disables the Chromium sandbox; Playwright evidence ran successfully with the repository harness's existing `--no-sandbox` path. These red/variance signals are recorded in the dossier rather than waived.
