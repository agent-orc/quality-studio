# Quality Studio frontend performance

The interaction budgets are contracts, not aspirations:

| Interaction | Budget | QS-9 measurement | Result |
| --- | ---: | ---: | --- |
| Tree expand | < 50 ms scripting-to-paint | 0.8 ms | Pass |
| Tree collapse | < 50 ms scripting-to-paint | 4.9 ms | Pass |
| File open / first visible content | < 150 ms | 25.5 ms | Pass |
| Review aspect switch | < 50 ms scripting-to-paint | 0.6 ms | Pass |
| Project dashboard open (5,000-file summary) | < 150 ms click-to-interactive | 65.8 ms | Pass |
| Repository transition visible | < 100 ms click-to-transition | 13.6–24.5 ms | Pass |
| Real-backend repository switch | < 500 ms click-to-usable | 131.3 ms cold UI / 73.3 ms SWR | Pass |
| Agent Studio lazy-root switch | < 500 ms click-to-usable | 23.4 ms median / 54.4 ms p95 | Pass |
| Lazy child expansion | < 100 ms request-to-paint | 50.3 ms | Pass |
| Cached tree toggle | < 50 ms scripting-to-paint | 2.4 ms median / 6.1 ms p95 | Pass |

Re-measured for QS-9 on 2026-07-11 in Microsoft Edge 150.0.4078.65 (Chromium), headless at 1600 × 1000. The file route returned 333,782 bytes, deliberately above the 200 KB acceptance boundary, together with two review documents. The view split the response into lines but inserted only 80 overscanned line rows. Tree expand/collapse inserted only the visible fixed-height window. Network time is included in the file-open mark because it starts at selection and ends on the first animation frame after visible content renders. The aspect switch reused the loaded file response; the request counter remained at two (initial file plus opened file) after switching.

QS-31 repeated the automated regression check on 2026-07-22 in Chromium 149.0.7827.55 at 1600 × 1000. First visible file content was 46.4 ms for the same 333,782-byte fixture. The harness also asserted that the explicit large-file mode was visible and that no highlighted tokens were produced above 200 KB. Syntax evidence for a supported-size C# file was captured from the production build in both the [dark theme](evidence/qs-31-syntax-dark.png) and [light theme](evidence/qs-31-syntax-light.png); each capture contains multiline strings/comments and four finding gutter markers.

QS-40 measured the project transition on 2026-07-25 in Chromium 149.0.7827.55 at 1600 × 1000. The deterministic dashboard response reported 5,000 files and retained only 30 hotspot rows; click-to-visible interactive tiles was 65.8 ms.

QS-54 measured repository switching on 2026-08-08 in Chromium 149.0.7827.55
at 1600 × 1000. Its second harness stage starts a real QualityStudio.Api and a
dedicated Angular proxy, creates clean Git repositories with realistic nested
TypeScript sources, and registers a 1,600-source-file target. No Playwright
route intercept is installed for this stage. The first target switch showed a
transition in 13.6 ms and had a usable dashboard plus tree in 131.3 ms. A
return switch reused the last browser snapshot, marked it as updating, and was
usable in 73.3 ms. The target dashboard's first interactive paint was 32.2 ms.
The backend prewarm phase measured 169.58 ms total for 1,603 tracked fixture
files (18.20 ms Git state, 45.46 ms hierarchy scan, 3.80 ms review-meta
discovery, and 102.07 ms projection).

The new contracts are `< 100 ms` from repository click to a visibly painted
transition and `< 500 ms` from click to a usable fresh dashboard and tree. The
usable budget is based on the 131.3 ms deterministic real-backend run and the
219.5–295.8 ms warm project-plus-tree measurements from the real
agent-taskboard repository, leaving host-variance headroom without admitting
the former multi-second wait. While revalidation continues, the dashboard
shows the last known per-repository snapshot with an explicit updating notice.
With no browser snapshot, a skeleton names the Git-state, repository-scan,
review-metadata, and projection phases.

QS-82 measured the real 3,927-file Agent Studio repository on 2026-08-12 in
Chromium 149.0.7827.55. Five switches using the v2 one-level root contract were
usable in 15.7–54.4 ms (23.4 ms median). The first project expansion fetched
ten children and painted in 50.3 ms; six later cached expand/collapse actions
measured 0.2–6.1 ms. Child requests carry the immutable root snapshot ETag,
so they skip redundant Git-state measurement. The explorer keeps keyboard and virtualized-row behavior,
while deep links and unloaded file lookup use the bounded server-side tree
search instead of forcing the recursive root response.

The editor surface is now a deferred standard component chunk. The production
initial bundle fell from 478.30 kB to 438.02 kB (40.28 kB / 8.42%), passing the
QS-59 target of at most 450 kB without changing the 480 kB error ceiling.

## Repeat the automated measurement

1. Run `npm start` (the harness defaults to `http://127.0.0.1:4200`; set `QS_URL` to use another URL).
2. Run `npm run perf` in `frontend/`.
3. The first Playwright stage intercepts the file API with a deterministic payload and review metadata, plus a bounded dashboard projection reporting 5,000 repository files. It protects the existing tree, file, aspect, and dashboard contracts.
4. The second stage launches its own real API and Angular proxy, creates and commits the realistic fixture repositories, waits for the registered-repository background prewarm, and performs three repository switches without API response interception. It writes `project-switch-perf.json` and light/dark transition screenshots to `JOB_RESULTS_DIR` (or `frontend/evidence` when the variable is absent).
5. The command exits non-zero if any existing budget, the 100 ms transition budget, the 500 ms usable-content budget, snapshot feedback, or file-refetch assertion fails.

The app also logs stable JSON events named `qs.tree.toggle`, `qs.file.first-content`, `qs.review.aspect-switch`, `qs.repository.transition-visible`, and `qs.repository.switch.usable`, including `durationMs`, `budgetMs`, and `withinBudget`. API fallback and tree load use `qs.data.demo-fallback`, `qs.data.file-demo-fallback`, and `qs.data.tree-loaded`.

## Verify with Chrome tracing

1. Open the app in a production Chrome build and DevTools → Performance.
2. Enable Screenshots and Web Vitals. Use 4× CPU throttling for a conservative check, then record.
3. Expand and collapse a repository row, stop recording, and inspect the click task through the following paint. Search the Timings track for `qs.tree.toggle`; its measure must remain below 50 ms.
4. Record again, select a file of 200 KB or less, and stop after its first lines appear. Find `qs.file.first-content`; it must remain below 150 ms. Confirm the Main track does not contain a whole-file highlighting task.
5. Save the trace with the browser version, CPU setting, payload size, and result. Compare scripting separately from transport when diagnosing a regression, but use the end-to-end measure for acceptance.

## Design constraints that protect the budget

- The tree is flattened in memory and renders an overscanned 40-row window.
- The code view renders an overscanned 80-line window regardless of file length.
- Finding ranges are indexed once per loaded review/aspect; only markers belonging to the visible 80-line window enter the DOM.
- Aspect switching selects an already-loaded `metaDocuments` entry and does not fetch file content again.
- The first-content path displays plain escaped text. Supported files up to 200 KB are tokenized only after that paint in a cancellable single-concurrency worker and delivered in 200-line chunks; whole-file main-thread highlighting is prohibited.
- Production bundle budgets are enforced at 350 KB warning / 480 KB error initially and 10/12 KB per component stylesheet. The attack matrix is a deferred 17 KB lazy chunk and is not paid on the editor's first-content path.
