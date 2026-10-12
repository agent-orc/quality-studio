# QS-122 status

Result: Done

## Cause

The `ERROR: HttpErrorResponse{… status: 503 … url: '/api/repos/default/tree' …}` line comes from one spec:
`frontend/src/app/core/api/api-interceptor.spec.ts` → **`API request policy` › `gives up after the configured number of retries`**.
The spec created `firstValueFrom(http.get('/api/repos/default/tree'))` and attached the rejection expectation only after its retry loop. The third 503 rejects the promise while the loop is still awaiting a 10 ms `setTimeout`. When zone.js drained its microtasks, it found a rejection without a handler and called `console.error(rejection)`. Karma prints that as `ERROR:`. Because Jasmine randomizes spec order, the line moves around (between specs 211/212 in the gate, spec 89 or 194 locally).

A trace taken in the browser confirms the source: `console.error` ← `api.onUnhandledError` ← `handleUnhandledRejection` ← `drainMicroTaskQueue` (zone.js). zone.js then also tries to dispatch a `PromiseRejectionEvent` so Jasmine can see it. That constructor throws for a raw rejection value (no `promise` member), and zone swallows the error. So Jasmine never failed the spec; the leak only showed up as a console line. The repository-tree `WARN` lines (`qs.repositories.unavailable`, `qs.data.navigation-target-unavailable`) are intended product logs and unrelated.

### Exit code 1

I could not reproduce exit 1 with all specs green:

- Current `origin/main` (`85d506ed`, 243 specs): `npm --prefix frontend test` → `TOTAL: 243 SUCCESS`, exit 0, with the `ERROR` line present.
- The QS-116 delivery `bf7278ff` (05.10., exactly **222 specs**): run with `CHROME_NO_SANDBOX=1` through the gate-shaped command `test_status=0; npm --prefix frontend test || test_status=$?; rm -rf .review-tmp; exit "$test_status"`, also with `TMPDIR=.review-tmp`. Each run gave `TOTAL: 222 SUCCESS`, exit 0, with the `ERROR` line present. Without `CHROME_NO_SANDBOX` the same commit exits 1 because Chromium aborts before any spec runs (fixed on main by QS-103 `85d506ed`). That is a different symptom.
- In karma 6.4.4, `BaseReporter.onRunComplete` prints `TOTAL: N SUCCESS` only when `results.error` and `results.disconnected` are both false and nothing failed. That is exactly when `calculateExitCode` returns 0. In the gate logs as quoted, karma's own exit code was therefore 0. The 1 must have come after karma reported (builder/`ng`/npm teardown, or the elided part of the gate command). The gate logs (`post-steps/pre-main-test-gate-*.log`) are not present on this runner (`tasks/QS-116` and `tasks/QS-104` contain only `results/`), so I could not determine that step.

The leaked rejection is the only defect in the frontend test run that the evidence shows. It is fixed at its source. Leaks like it can no longer stay silent.

## Change

- `api-interceptor.spec.ts`: the spec now attaches `expectAsync(…).toBeRejectedWith(objectContaining({ name: 'HttpErrorResponse', status: 503, url: '/api/repos/default/tree' }))` before the retry loop and awaits it afterwards. The expected 503 is handled and asserted, and nothing is logged.
- New `frontend/src/testing/unhandled-rejections.spec.ts` (test-only, excluded from the app build by `tsconfig.app.json`): top-level `afterEach` and `afterAll` hooks record every rejection zone.js reports as unhandled. The leaking spec fails with `unhandled promise rejections; … 'HttpErrorResponse 503 for /api/repos/default/tree'`; a leak after the last spec fails the run through `afterAll`. Errors are not ignored globally: zone's console line is replaced by a spec failure, so real failures still fail. The gate command is unchanged.
- Focused specs `unhandled promise rejection guard`: one checks that a deliberately unhandled `HttpErrorResponse` 503 for `/api/repos/default/tree` is recorded; the other checks that a handled one is not.
- CHANGELOG entry under Unreleased.

## Verification

| Command | Result |
| --- | --- |
| `npm --prefix frontend test` on `origin/main` before the fix | 243/243 SUCCESS, exit 0, one `ERROR: HttpErrorResponse … 503 … /api/repos/default/tree` line. `main-before-fix.log` |
| Same command on QS-116 `bf7278ff` (222 specs, gate-shaped, no-sandbox) | 222/222 SUCCESS, exit 0, same `ERROR` line. `qs116-baseline-222-specs.log` |
| Old spec + guard (negative check) | Exit 1: `gives up after the configured number of retries FAILED`, rejection named. `guard-negative-check.log` |
| `bash -lc 'test_status=0; npm --prefix frontend test \|\| test_status=$?; rm -rf .review-tmp; exit "$test_status"'` after the fix | **245/245 SUCCESS, exit 0, no `ERROR` line.** `frontend-test-full-run.log` |
| `npm --prefix frontend test` second run (another random order) | 245/245 SUCCESS, exit 0, no `ERROR` line |
| `npm --prefix frontend run lint` | Exit 0. `frontend-lint.log` |

Logs are in the job results directory. The result SHA is recorded in the job results copy of this file.
