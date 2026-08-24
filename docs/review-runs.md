# UI review runs

Quality Studio starts Runner reviews through the API host. The browser never launches an agent process. `POST /api/review` (or the repository-scoped equivalent) validates the selected hierarchy node, records its descendant file plan, enqueues the work, and immediately returns `202 Accepted` with a run ID.

## Preflight estimate

Before confirmation the browser calls `POST /api/review/estimate` (or the repository-scoped equivalent) with the selected CLI, model, kind, path, and cap. The response is computed from the exact file and aggregate prompts that `ReviewRunner` will send. Rendered prompt characters are converted to input tokens at four characters per token. The output/input ratio comes from matching operations in `.quality/usage/`, falling back to 20% when there is no history. The response identifies its history sample count and method, so a fallback is never presented as measured precision.

Cost is computed by `CodingAgentRunner.Pricing.ModelPriceCatalog.Default` for the selected model. An unknown or currently unpriced model returns an explicit price status and no cost. A cost cap is rejected in that case; a token cap remains available.

Every completed run compares its preflight estimate with usage actually recorded by that sweep. `deviation.inputTokensPercent`, `deviation.outputTokensPercent`, and, when priced, `deviation.costPercent` are signed percentages (positive means actual was higher), and the run row displays them. This is the acceptance comparison against the recorded sweep, not a precision claim: CLI-added system context, provider tokenization, cache behavior, and response length are not knowable from prompt characters. Capped or failed partial runs do not publish a misleading full-sweep deviation.

The server-side acceptance test exercises a two-file plus aggregate sweep through the HTTP API, records each operation in `.quality/usage/`, deliberately crosses a token cap, verifies the reviewed/skipped report, raises the cap, and verifies that the completed run reports a non-zero estimate deviation. The assertion intentionally checks deviation rather than equality.

## Freshness, caps, and execution

The API runs uncapped file reviews with bounded concurrency (`ReviewJobs:MaxConcurrency`, default `2`). Capped runs execute serially so concurrent files cannot all cross the boundary. Container sweeps continue after individual file failures and write the selected project, module, or namespace review after all file attempts finish.

Before every file and aggregate operation, the runner compares the current subject manifest hash, effective review-inputs hash, and requested model with the existing sidecar. The comparison reads the sidecar's `reviewer.model`, which every schema version carries. `review-meta.v3` additionally records what was asked for as `reviewer.requestedModel` and `reviewer.requestedThinkingLevel`, described in [`concept.md`](concept.md#review-meta-schema-v3). A unit is `skipped-fresh` only when all three match, and the agent is not called. The run snapshot and durable status include these skips; aggregate freshness is exposed through `aggregateState`. Set `force: true` on the start or estimate request to bypass this gate for every unit in the run.

A run may use one token cap or one cost cap. Omitting both inherits the repository's default. Enforcement happens in `ReviewJobService` at durable review-operation boundaries: once recorded usage reaches the cap, no next file or aggregate operation starts. The operation that crosses the threshold is allowed to finish cleanly, so actual spend can exceed the cap by at most that operation. Remaining files are persisted as `skipped`, the aggregate is reported as `skipped` when applicable, and the run ends as `capped` with a stop reason and complete reviewed, failed, and skipped counts.

A capped run is resumable without repeating completed files. `POST /api/review/runs/{id}/resume` accepts a higher `{ "tokenCap": ... }` or `{ "costCap": ... }`. Skipped units return to `queued`, while done and failed units remain durable. The server rejects a replacement cap already below current spend. Repository defaults are configured with `defaultReviewTokenCap` or `defaultReviewCostCap` (mutually exclusive) in the repository registration UI or API.

## Module and project passes

A container run reviews every descendant file first and then writes the selected
project, module, or namespace review as one further operation of the same run.
The aggregate operation therefore reads the file sidecars that run has just
written, and a member reviewed a moment ago appears in it with its new grade.

Module and project reviews have their own prompt templates. `ReviewPromptBuilder`
resolves `(level, kind)` against the embedded templates, so a module `code`
review runs `module-code-review` and a project `security` review runs
`project-security-review`. A level and kind with no template of its own falls
back to the file template - `performance` at every aggregate level, and
`namespace` and `function` everywhere - and `reviewInputs.prompt.id` records the
template that actually ran rather than the one the level would like to have. The
template hash flows into `reviewInputs.effectiveHash` as before, so changing an
aggregate prompt makes aggregate sidecars policy-drift without touching file
sidecars.

The aggregate code prompts ask for the aspects `architecture`, `structure`,
`boundaries`, `duplication` and `consistency`. The aggregate security prompts are
a threat analysis: entry points grouped by trust level, authorisation chains from
an entry point to process, filesystem, network, secret or evaluation surfaces,
trust boundaries, and the sensor-backed secrets and dependency picture. They
close with a plan. The plan needs no new schema field: each finding's
`recommendation` ends with a line of the form `Priority: P1 | Effort: M`, and the
`summary` ends with the mitigation titles in execution order.

### The subject digest

An aggregate review does not receive the concatenated source of its members. It
receives a digest built by `AggregateSubjectDigest` within a character budget of
80,000 by default:

- a header with the member count, total lines and characters, and how many
  members currently have a review of this kind;
- one table row per member with its path, size, owning derived unit, current
  grade and finding counts by severity, read from the file sidecars through
  `ReviewMetaJson`;
- the paths excluded from the aggregate, with their reasons;
- the derived module and namespace structure, passed in by the planner from the
  hierarchy it already holds, so the runner never derives the hierarchy twice;
- the findings already recorded for the members, each with the fingerprint an
  aggregate finding can cite;
- for a security pass, the derived boundary inventory from
  `.quality/boundaries/inventory.json`, scoped to the members at module level and
  whole at project level, or an explicit statement that none has been scanned;
- real source under the remaining budget, at least a quarter of it. Each member
  is allocated a share proportional to its size with a floor, so a monolith
  cannot crowd out its neighbours. A member whose text fits its share is included
  whole; a larger one is reduced to its declaration lines. Every line carries its
  real one-based number, so a range the agent cites is a range in the file.

The digest changes what the agent reads, not what the run considers current.
`reviewedHash` remains the manifest of member subject hashes and aggregate
control files defined in [`concept.md`](concept.md#exact-hashing-contract).

### Cross-file findings

An aggregate review reports a defect class once, with every occurrence in its
`locations` array. The runner anchors each occurrence it can resolve: the first
becomes the `primary` anchor as in a file review, and every further one becomes a
`related` anchor with its own captured excerpt and hashes, plus an `observed`
`sourceSpan` evidence item pointing at it.

A finding may cite the member-file findings it generalises. The agent lists their
fingerprints in `relatedFindings`; the runner checks each against the member
sidecars the digest read, records it as a `legacyClaim` evidence item whose
status is `observed` when it matched and `unverified` when it did not, and
removes the array before writing. `review-meta.v3` findings allow no additional
property, and none is needed.

## Durable state

Run orchestration is durable under `<repository>/.quality/runs/<runId>/`:

- `manifest.json` is the immutable enqueue-time plan. It records the selected node and level, kind, model, CLI type, force flag, preflight estimate, initial cap, aggregate controls, and every target file with its subject hash.
- `progress.jsonl` is an append-only file-transition log. Each flushed line records the run and file path, state, timestamps, and any error. Recovery ignores an incomplete line left by a crash and continues from the other records.
- `status.json` is the current overall state and its counters, cursor, timestamps, errors, usage, live cost, current cap, aggregate state, and stop reason. It is replaced atomically through a same-directory temporary file.
- `result.json` is the stable evidence projection refreshed atomically with run state. It
  records the chosen `model`, `thinkingLevel`, and `cli` (using explicit
  `runner-default` / `model-default` markers when no override was chosen), together with
  scope, outcome, counts, usage, and cost status for downstream Token Economy analysis.
- `observations.json` is the orchestration checkpoint for exact file and aggregate
  observations. It is replaced atomically before the corresponding progress
  transition is appended, allowing recovery to publish the same captured evidence.

At every terminal transition the API projects these immutable inputs into
`.quality/reports/runs/<runId>.json`. This canonical, strict-schema snapshot is
repository-owned durable history. A capped run publishes revision 1; resuming and
finishing that run publishes a higher revision without repeating already completed
operations. Renderers, API downloads, CLI gates, and run trends all consume this
snapshot rather than mutable current sidecars. See
[`quality-reports.md`](quality-reports.md) for formats and trend semantics.

Model options come from the governed Token Economy snapshot described in
[`model-catalog-integration.md`](model-catalog-integration.md). The model and optional
thinking-level override are persisted in the manifest before enqueue and passed to the
same CodingAgentRunner request used for every file and aggregate operation.

At startup the API scans the registered repositories for durable runs before host
readiness completes. `queued` and formerly `running` runs are enqueued again; a
file recorded as `done`, `failed`, or `skipped-fresh` is not reviewed again. A
file that was `running` when the process stopped is returned to `queued`, because
its sidecar write cannot be assumed to have completed. `paused` runs are restored
but remain idle. Terminal `done`, `failed`, `cancelled`, and `capped` runs are
loaded into recent history without being resumed.

The UI polls `GET /api/review/runs` every 1.5 seconds only while a run is queued or running. Each operation's recorded input/output usage is priced and persisted immediately, so the run row shows live tokens or cost spent against the cap. A terminal transition refreshes the hierarchy and the open file, so sidecar grades and staleness decorations update without a page reload. `POST /api/review/runs/{id}/pause` stops active work at the cancellation boundary while preserving completed files. Repository-scoped forms of all routes are also available. `DELETE /api/review/runs/{id}` permanently cancels queued, paused, or active work.

`.quality/runs/` is ignored by Git because it is disposable orchestration working
data. Review sidecars remain the committed current-state truth. Canonical run
reports preserve portable export evidence for each terminal execution, while the
tracked archive below owns operational history and deterministic run comparison.

## Tracked terminal archive and history

Terminal run evidence is independently persisted under
`.quality/run-history/YYYY-MM/<runId>/`. `run.json` is an immutable enqueue-time
manifest, including resolved model, thinking level, and CLI; `operations.jsonl`
and `findings.jsonl` are append-only, idempotent facts; and `attempts/0001.json`,
`0002.json`, and later attempt records are create-only. Each attempt records both
its own and cumulative counters and spend.
The versioned contracts are in `schemas/run-*.v1.schema.json`. Quality Studio
never stages or commits these files; repository owners retain normal Git control.

At startup, present v0 `.quality/runs/` folders are conservatively migrated into
the tracked format. Migration is idempotent and records provenance. Missing
historical grades, verdicts, findings, or usage remain unknown rather than being
invented. A fresh clone therefore retains terminal history even when the ignored
recovery cache is absent.

`GET /api/review/history` reads this archive on demand with cursor pagination and
optional exact `kind`, `path`, and `outcome` filters. `GET
/api/review/history/{id}` returns an attempt detail; `GET
/api/review/history/{id}/diff?against=<olderId>` returns deterministic scope,
input, execution, grade, typed-verdict, finding, and economy deltas. Corrupt
archive entries remain visible as typed history errors instead of disappearing.
Repository-scoped forms are available under `/api/repos/{repoId}`. The UI keeps
live controls on the operational routes and lazy-loads archived history and
two-run comparison only when opened. Ledger groups without any matching archive
are listed as `legacy-usage-only` with their known model, path, kind, level, and
token spend. They cannot open detail or comparison because no run plan, outcome,
quality evidence, or run timestamps can be proven.
