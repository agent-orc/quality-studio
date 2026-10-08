# QS-117 shadow comparison — Agent Studio code-quality aspect

**Result: 5/20 exact verdict agreements (25.0%).** The 15 disagreements are observations for manual adjudication. No Agent Studio gate consumed a Quality Studio verdict.

## Cohort and method

- Source: the operator’s 2026-10-07 export of Agent Studio records whose code-quality aspect reviewed exactly each `resultSha`. The 20-task cohort deliberately balances ten `concerns` and ten `pass` records. It is a diagnostic sample, not an estimate of production prevalence.
- Fetched `origin/develop` from `agent-orc/agent-studio`; all 20 result SHAs were visible in the remote result refs, all base/result objects were present locally, and `git merge-base --is-ancestor` passed for every pair. The comparison used the exact base-to-result range through `POST /api/repos/default/change-review`, with that checkout registered as `default`.
- `AGT-2950` was replaced by operator-provided `AGT-3014` before scoring because its binary diff returned `provider-unavailable`. The final cohort contains 20 completed `pass`, `concerns`, or `block` Quality Studio verdicts. The binary result is excluded from the denominator.
- Each response was retained in `qs-verdicts/<task>.json`; `shadow-pairs.json` binds the exported aspect verdict to both SHAs. `node scripts/compare-shadow-verdicts.mjs results/shadow-pairs.json results/shadow-comparison.json` produced the machine-readable counts. Paths are relative to this report.
- Model: `gpt-6-luna`, low thinking level. Policy hash: `sha256:709fe0fe23ef6adff316166e3c8e13a12774064eb119512b13a9d3f2484c7764`. Rule-set hash: `sha256:c4ae562e5e318dbe1fd9ffba7665e53740a022796589059113905422469d76c0`. These are constant across all 20 responses.

## Per-record results

| Task | Base → result | Agent Studio | Quality Studio | Rule-cited findings | Exact match |
| --- | --- | --- | --- | --- | --- |
| AGT-2944 | `02d5dde3` → `d467cf31` | concerns | pass | none (0) | no |
| AGT-2943 | `53d78664` → `380403bd` | concerns | pass | none (0) | no |
| AGT-3025 | `53d78664` → `7be76584` | concerns | block | QS-GN-002 (2) | no |
| AGT-2926 | `b225a6e7` → `df3e5365` | concerns | block | QS-GN-002 (1) | no |
| AGT-3015 | `cb51e48a` → `40c5cae3` | concerns | pass | none (0) | no |
| AGT-2934 | `a0dacb7d` → `bbf3298e` | concerns | pass | none (0) | no |
| AGT-2996 | `a0dacb7d` → `90a2893f` | concerns | block | QS-GN-001 (1) | no |
| AGT-2954 | `a0dacb7d` → `fe9f0421` | concerns | pass | none (0) | no |
| AGT-2964 | `48faf526` → `f00cdccc` | concerns | pass | none (0) | no |
| AGT-3001 | `0acd12a5` → `532ae09d` | concerns | pass | none (0) | no |
| AGT-3014 | `0acd12a5` → `849c8e94` | pass | pass | none (0) | yes |
| AGT-3002 | `79668c04` → `4562c060` | pass | block | QS-CS-003 (2) | no |
| AGT-2963 | `79668c04` → `eb6bc510` | pass | block | QS-GN-001, QS-GN-002 (3) | no |
| AGT-2938 | `b0c42992` → `bb874c0d` | pass | block | QS-GN-002 (2) | no |
| AGT-2946 | `475993f4` → `774e0d1d` | pass | pass | none (0) | yes |
| AGT-3000 | `e6dd1fe9` → `75f173b9` | pass | block | QS-GN-002, QS-GN-003 (3) | no |
| AGT-2929 | `53d78664` → `f1d72d69` | pass | block | QS-GN-001 (1) | no |
| AGT-3016 | `90f6a1ed` → `54ea5c04` | pass | pass | none (0) | yes |
| AGT-2985 | `4d4bd717` → `15424974` | pass | pass | none (0) | yes |
| AGT-2797 | `4d4bd717` → `f173c1ed` | pass | pass | none (0) | yes |

## Disagreements

| Agent Studio → Quality Studio | Count | Tasks | Reading |
| --- | ---: | --- | --- |
| concerns → pass | 7 | AGT-2944, AGT-2943, AGT-3015, AGT-2934, AGT-2954, AGT-2964, AGT-3001 | Quality Studio emitted no changed-line, rule-cited finding. |
| concerns → block | 3 | AGT-3025, AGT-2926, AGT-2996 | Quality Studio emitted at least one high-severity finding that the versioned policy blocks. |
| pass → block | 5 | AGT-3002, AGT-2963, AGT-2938, AGT-3000, AGT-2929 | Quality Studio emitted a high-severity, policy-blocking finding; these need code-path review before either verdict is treated as correct. |

### Record notes

- **AGT-2944 (concerns → pass):** No Quality Studio finding. Agent Studio reported a failed onboarding path that can retain an unvalidated host record.
- **AGT-2943 (concerns → pass):** No Quality Studio finding. Agent Studio described the diff as clean overall.
- **AGT-3025 (concerns → block):** QS-GN-002 at `runner/GitWorkspace.cs:155`: The recovery marker is read with `File.ReadAllTextAsync` without a size limit, then deserialized. A marker file in the retained worktree can therefore force unbounded memory use when a later claim tries recovery. Enforce a byte limit before reading and refuse oversized markers.; QS-GN-002 at `runner/PushProtection.cs:32`: `ParseGitHubRejection` splits and scans the complete stderr string with no size or line-count bound. A remote-controlled rejection response can drive unbounded allocation and parsing work. Bound the captured rejection size and refuse oversized input.
- **AGT-2926 (concerns → block):** QS-GN-002 at `backend/Features/Pipeline/BatchGate/BatchGatePilotService.cs:896`: The per-task fallback invokes the gate with a 45-minute timeout value, but this method does not enforce a wall-clock bound around the awaited gate call. A runner that ignores its timeout can leave the task in Auto Review and the fallback flight running indefinitely.
- **AGT-3015 (concerns → pass):** No Quality Studio finding. Agent Studio noted misplaced doc comments and a whole-file line-ending rewrite.
- **AGT-2934 (concerns → pass):** No Quality Studio finding. Agent Studio said prior routing and null-check blocks were resolved.
- **AGT-2996 (concerns → block):** QS-GN-001 at `backend/Features/Git/GitService.cs:5130`: `branch` is caller supplied, but `IntegrationLaneRef` interpolates it directly into a Git ref and `UpdateLaneRef` passes that ref to `git update-ref`. `IsLikelyBranchName` is checked in the caller, but this helper itself constructs the ref without validating the branch; callers such as `IntegrationLineRef` can also return the raw value. Verify every route into these Git arguments enforces a strict branch-name boundary, or validate and safely construct the ref here.
- **AGT-2954 (concerns → pass):** No Quality Studio finding. Agent Studio said prior blocking findings were resolved.
- **AGT-2964 (concerns → pass):** No Quality Studio finding. Agent Studio said budget clearing and production wiring were fixed.
- **AGT-3001 (concerns → pass):** No Quality Studio finding. Agent Studio noted a curve can join readings across a reset and unused loading state.
- **AGT-3002 (pass → block):** QS-CS-003 at `backend/Features/Pipeline/IntegrationVerification/MergeIntoDevelopRunner.Verification.cs:47`: `VerifyContainedDeliveryAsync` performs awaited gate I/O but accepts no `CancellationToken`, so callers cannot cancel verification. Add a token parameter and pass it through to the gate call.; QS-CS-003 at `backend/Features/Pipeline/IntegrationVerification/MergeIntoDevelopRunner.Verification.cs:102`: `RunVerificationGateAsync` performs awaited gate I/O but accepts no `CancellationToken`. Add a token parameter and pass it to the gate runner.
- **AGT-2963 (pass → block):** QS-GN-002 at `frontend/e2e/mockups/usage-header.spec.ts:29`: The mock server decodes the caller-controlled URL path and joins it to the build directory without checking that the resolved path remains inside that directory. A path containing encoded traversal segments can therefore make the server read and serve files outside the mock bundle. Normalize and constrain the resolved path before reading it.; QS-GN-001 at `frontend/src/app/features/studio-shell/studio-shell.header-cockpit.ts:71`: The saved CLI preference is read from localStorage, but this class can run during server rendering where localStorage is unavailable. That can throw while computing the header’s default CLI and prevent the shell from rendering. Guard access for non-browser environments.; QS-GN-002 at `frontend/src/app/features/usage-cockpit/services/usage-cockpit.service.ts:56`: Each refresh unsubscribes the previous request, but the service does not place a timeout on the HTTP request. If the request stalls without completing, the snapshot can remain loading indefinitely and polling repeatedly replaces the stalled request. Bound request duration and report the timeout as a failed read.
- **AGT-2938 (pass → block):** QS-GN-002 at `backend/Features/Runner/RunnerEndpoints.cs:79`: The receipt feed has a limit of 200 tasks, but each task can contribute an unbounded number of history facts before grouping. A task with a large receipt history can make this request consume excessive memory and CPU. Enforce a receipt-count bound while reading or projecting history, and return a clear refusal or bounded result when it is exceeded.; QS-GN-002 at `task-server/TaskServerStudioRunnerOrchestratorStore.cs:521`: The feed bounds tasks to 100, but reads every history fact for each selected task and accumulates them in receiptRows before the final boundedLimit is applied. Histories can grow without bound, so a single feed request can perform unbounded work and allocation. Bound receipt rows during projection or at the query boundary.
- **AGT-3000 (pass → block):** QS-GN-002 at `backend/Features/Git/StaleLocks/GitLockOwnerProbe.cs:184`: The Linux process inventory accumulates one observation per matching git process without a count bound. On a host with many processes, each lock check can retain an unbounded list and consume memory; cap the inventory and treat overflow as unknown ownership.; QS-GN-002 at `backend/Features/Git/StaleLocks/GitStaleLockGuard.cs:350`: Recursive ref-lock discovery materializes every matching file with `ToList()` without a count limit. A repository with an unusually large or adversarial refs tree can make each write retain an unbounded path collection; stop at a configured cap and fail closed if exceeded.; QS-GN-003 at `docs/fluid-capacity/index.html:1`: This adds a document under `docs/fluid-capacity`, outside the repository's declared documentation source locations. The new source is therefore returned from a retired or unowned location under the architecture contract.
- **AGT-2929 (pass → block):** QS-GN-001 at `backend/Features/Tasks/Decisions/DecisionApplyPolicy.cs:86`: Caller- or model-supplied decision fields are interpolated directly into a prompt block. A question, option label, rationale, title, or record path containing Markdown or instruction text can escape its intended field and alter the implementation card’s prompt. Escape or structurally delimit these values before appending the block.

Agent Studio’s code-quality aspect produced confirmed false blocks on `AGT-2917`, `AGT-2932`, `AGT-2972`, and `AGT-3008` according to the operator’s 2026-10-07 note. Those four subjects are **not in this export or denominator**. They are a separate caution against treating an Agent Studio disagreement as proof that Quality Studio is wrong.

The five `pass → block` records and three `concerns → block` records require human tracing of the cited paths, rule applicability, and policy severity. The seven `concerns → pass` records require checking whether the aspect concern is an introduced defect covered by the enabled catalogue. This report does not promote either reviewer to gate authority; QS-W6 remains in force pending a dossier decision update.
