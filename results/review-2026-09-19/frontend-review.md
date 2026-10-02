# Frontend quality review - 2026-09-19

Scope: frontend/ in Quality Studio. No commits or pushes. Browser smoke intercepted every API request and used deterministic fixtures. No actual repository data, review runs, external services or credential values were involved.

## Fixed findings

| Priority | Finding and consequence | Change | Regression proof |
| --- | --- | --- | --- |
| P1 defense in depth | The global HttpClient interceptor sent the stored bearer token to every URL, overwrote unrelated Authorization headers, and opened the credential dialog on unrelated 401 responses. No current external HttpClient call site was found: this was a missing boundary, not a demonstrated live leak. | Credentials and API recovery apply only to normalized same-origin /api or /api/ URLs. Other paths, external URLs, protocol-relative URLs, URL credentials and backslashes are excluded. | Two new specs failed before the fix and pass now: external origins, wrong prefixes, literal/encoded traversal, unrelated token and external 401. |
| P2 availability | Denied browser storage crashed shell creation. Storage write failure aborted repository switching before the API call and threw during theme changes. | Optional theme/repository preference reads and writes tolerate storage errors and keep session state working. | Two specs failed before the fix and pass now. Browser smoke blocks the localStorage getter itself, then verifies startup, theme and repository switching. |
| P2 navigation | Back across a repository boundary replaced the restored file with the dashboard and created a new history entry. A late switch could reopen an older selection. | Switches restore the history path/finding, including unloaded tree levels. Superseded operations cannot replace newer selections. | Two specs failed before the fix and pass now. Browser Back/Forward verifies delayed tree responses and unchanged history length. |
| P2 development supply chain | npm reported four vulnerable development dependencies (one high, three moderate). Production audit was already clean. | Compatible transitive lockfile updates only: hono 4.13.3 -> 4.13.8, js-yaml 4.3.1 -> 4.3.2, body-parser 1.20.6 -> 1.20.8, qs 6.15.3 -> 6.16.0. No direct version changes or overrides. | Dry-run inspected first. Complete and production audits report zero known vulnerabilities. Build, lint and complete component suite rerun after updates. |

Production files changed: src/app/core/api/api-interceptor.ts, src/app/shell/app.ts, package-lock.json and API-scope wording in README.md.

Regression coverage: api-access.spec.ts, shell/app.spec.ts and new shell/app-storage.spec.ts. Shell tests preserve the previous stored repository so random order does not leak preferences between tests.

The hosted report export regression was also fixed: ordinary anchor navigation bypassed the bearer interceptor, so hosted downloads had no Authorization header. Run and repository reports now use typed HttpClient Blob requests. The UI downloads application/octet-stream object URLs rather than opening executable same-origin HTML, releases each URL after the browser claims it, prevents duplicate in-flight exports, and displays failures. No credentials appear in a URL.

Additional files: core/api/review-runs-api.ts, core/api/quality-api.ts and reviews/run-history (component/template/styles plus tests); the screenshot helper selector was updated from anchors to buttons.

## Validation

| Check | Result | Evidence |
| --- | --- | --- |
| Complete Angular/Karma suite in installed Edge | 215 passed | [Summary](frontend-tests-green.log) |
| Angular coverage suite | 215 passed; line coverage 75.00%, baseline 60.82%, floor 58.82% | [Suite](frontend-coverage-green.log), [CI baseline check](frontend-coverage-validation.log) |
| Style reference production build | Passed; 155.07 kB initial | [Log](frontend-reference-build.log) |
| ESLint | Passed | [Log](frontend-lint-green.log) |
| Import-boundary/typography checks | 30 passed | [Log](frontend-architecture-green.log) |
| Browser resolver checks | 4 passed | [Log](frontend-browser-resolver-green.log) |
| Production build and bundle budgets | Passed | [Summary](frontend-build-green.log) |
| Complete npm audit | 0 vulnerabilities | [JSON](frontend-npm-audit-after.json) |
| Production npm audit | 0 vulnerabilities | [JSON](frontend-npm-audit-production-after.json) |
| Browser smoke | Mocked GETs only, zero page exceptions | [JSON](frontend-browser-smoke.json), [log](frontend-browser-smoke.log) |
| Frontend whitespace check | Passed | git diff --check -- frontend |

Existing build warnings remain: two Angular NG8102 diagnostics in editor.html and Prism CommonJS optimization warnings. Initial bundle stays below warning/error budgets.

Default npm test selected a browser that did not connect to Karma (three 60-second capture attempts). Explicitly selecting the installed Edge resolved the environment issue; no sandbox-disabling flags were needed.

~~~sh
cd frontend
CHROME_BIN=C:/Progra~2/Microsoft/Edge/Application/msedge.exe npm test
npm run lint
npm run test:architecture
npm run test:browser-resolver
npm run build
npm audit --json
npm audit --omit=dev --json
~~~

## Before/after evidence

Eight new regression tests were run against unchanged production code first:

- [Token boundary: 2 failures](frontend-token-red.log)
- [Blocked storage: 2 failures](frontend-storage-red.log)
- [Repository history/race: 2 failures](frontend-history-red.log)
- [Hosted report exports: 2 failures](frontend-export-red.log)
- [Dependency update dry-run](frontend-audit-dry-run.log), [applied update](frontend-audit-fix.log)

Reproducible browser smoke: [frontend-browser-smoke.mjs](frontend-browser-smoke.mjs). Run against a standalone frontend; the script mocks every API route and asserts GET requests:

~~~sh
cd frontend
npm start -- --host 127.0.0.1 --port 4229
# In a second shell:
QS_URL=http://127.0.0.1:4229/ node ../results/review-2026-09-19/frontend-browser-smoke.mjs
~~~

Screenshots generated and visually inspected:

- [Restored repository/file/security finding after Back -- mocked](frontend-history-restored--mocked.png)
- [Usable light-theme dashboard with storage blocked -- mocked](frontend-storage-blocked-usable--mocked.png)
- [Hosted export failure with malicious model text safely displayed -- mocked](frontend-export-error--mocked.png)

## Security design decisions

The credential still uses localStorage as documented. The interceptor fix constrains where the app attaches it; it cannot protect the token from scripts executing in the same origin. A hosted deployment should decide on the session model and CSP together, including replacing long-lived browser bearer tokens with a server-side session.

The iframe preview contract still sends navigation metadata to the parent with a wildcard target origin. Repository IDs, paths and finding identifiers can be sensitive metadata even when source bodies and credentials are excluded. Hosted deployments should restrict embedding and allowed parent origins. These compatibility-sensitive decisions belong to the broader security concept.

## Additional security inspection

- Findings, source, discussion text, model identifiers and captured run evidence use Angular text interpolation. No innerHTML, DOM HTML insertion, sanitizer bypass, or runtime Markdown-to-HTML renderer was found under src/app. The browser smoke supplies img/onerror and javascript-Markdown payloads in finding/model fields: their text remains visible, no injected img appears and no payload executes.
- Runtime source highlighting produces token text and CSS classes; it does not inject rendered HTML. Source paths become navigation query parameters, not external window locations. External reference links come from generated static catalogues and use Angular href binding plus noopener/noreferrer.
- API repository, run, guideline and suppression identifiers use encodeURIComponent; source paths and compare/trend values use HttpClient query parameters. The new export regression also exercises a run ID containing spaces and a slash.
- Report HTML is downloaded without navigation or a popup. Smoke captures actual browser downloads, verifies the filename/content and unchanged document URL, checks two created octet-stream object URLs are both revoked, then verifies a repository 403 displays an error and retry succeeds.
- Backend run-report HTML uses WebUtility.HtmlEncode for dynamic fields plus a restrictive meta CSP. Repository HTML encodes its rendered Markdown into a pre element. No executable HTML injection was demonstrated in either inspected renderer.
- Markdown exports retain Markdown/raw HTML content for downstream viewers; this app downloads them and does not render them. A downstream Markdown previewer must apply its own sanitizer.

## Remaining limits and follow-up effort

1. Report size is not bounded by this frontend change. The API report endpoints render complete strings, and QualityRunReportStore.Load reads the full JSON file. The client buffers the response as a Blob; only one export per drawer is in flight, with the existing 180-second per-attempt report timeout. A server byte limit/streaming strategy is still needed for very large reports (roughly 1-2 engineering days, plus representative volume tests). No load-induced denial was claimed or tested.
2. Persistent bearer storage and parent-origin embedding policy remain the hosted-deployment design decisions noted above. Removing localStorage tokens needs an agreed session model, not just a browser storage substitution.
3. Long hostile model text is escaped safely but can widen the narrow run-history area, causing horizontal clipping. Controls remain usable in the smoke test. This is a low-priority overflow/typography follow-up, approximately half a day with narrow-width validation; no styling expansion was made in this security patch.

Final smoke evidence uses 93 intercepted GET requests and reports zero page exceptions or Angular runtime errors. All screenshots carry --mocked provenance. The initial incomplete run fixture lacked the required files array; it was corrected before this passing result and was not classified as an application defect.

## Real-API performance follow-up

The existing project-switch-perf harness passed against the Release backend with 1,600 generated source files and isolated app data: three usable switches took 91.4 / 76.6 / 222.5 ms (500 ms budget), transitions 20.6 / 15.8 / 12.7 ms (100 ms budget), first interactive realistic dashboard 39.6 ms (150 ms budget). Node 24.18.0 was used rather than the repository's pinned Node 22; concurrent .NET test work ran in the background. No retry or product changes were needed. Both screenshots were visually inspected; the harness completed its own process and temporary fixture cleanup.

[Detailed performance results and provenance](performance/README.md).

## Pinned runtime follow-up

An official portable Node 22.23.1 archive was verified against its published SHA256 and used without global installation or replacing node_modules. The complete Edge coverage suite passed all 215 tests, CI line coverage passed at 74.88% (60.82% baseline, 58.82% floor), and the production build passed. The temporary runtime was removed afterward; the global Node 24 installation is unchanged.

[Node 22 provenance, validation logs and cleanup](node22/README.md).

## Independent sensor follow-up

A final read-only backend report audit reproduced incomplete/malformed SARIF being accepted as available with zero findings in 14 isolated API probes. This baseline was handed to the security agent for the scoped correction. Coverage ingestion status also accepts unrelated XML, while coverage projection correctly remains unknown/null. No new product changes were made by the frontend reviewer.

[Before-fix sensor evidence and supported-schema boundaries](sensor-report-audit/README.md).

The subsequent SARIF correction was independently rechecked against a copied fresh Release API: all 16 expected responses passed. Seven formerly accepted incomplete/invalid reports now return unavailable, valid empty scans and genuine findings remain accepted, and coverage behavior is unchanged. [Before/after verification](sensor-report-audit/after/README.md). Own processes and temporary data were removed.

## Performance confirmation after Git hardening

The unchanged real-API switch harness was repeated at 21:50:52 UTC after Git/fsmonitor hardening, before the later npx command-runner extension, with Node 24/Edge and 1,600 generated source files. These measurements do not claim to cover identical final end-of-review DLLs. All budgets passed: transitions 18.9 / 18.0 / 14.5 ms (<100), usable switches 93.3 / 192.5 / 177.9 ms (<500), first interactive realistic dashboard 36.9 ms (<150). Root coverage ran concurrently. Both screenshots were visually checked; the harness exited successfully and completed its own cleanup. No retry or product changes were needed.

[Performance evidence and exact build provenance](performance-final/README.md); the earlier run remains preserved separately.
