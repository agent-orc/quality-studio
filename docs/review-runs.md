# UI review runs

Quality Studio starts Runner reviews through the API host. The browser never launches an agent process. `POST /api/review` (or the repository-scoped equivalent) validates the selected hierarchy node, records its descendant file plan, enqueues the work, and immediately returns `202 Accepted` with a run ID.

## Preflight estimate

Before confirmation the browser calls `POST /api/review/estimate` (or the repository-scoped equivalent) with the selected CLI, model, kind, path, and cap. The response is computed from the exact file and aggregate prompts that `ReviewRunner` will send. Rendered prompt characters are converted to input tokens at four characters per token. The output/input ratio comes from matching operations in `.quality/usage/`, falling back to 20% when there is no history. The response identifies its history sample count and method, so a fallback is never presented as measured precision.

Cost is computed by `CodingAgentRunner.Pricing.ModelPriceCatalog.Default` for the selected model. An unknown or currently unpriced model returns an explicit price status and no cost. A cost cap is rejected in that case; a token cap remains available.

Every completed run compares its preflight estimate with usage actually recorded by that sweep. `deviation.inputTokensPercent`, `deviation.outputTokensPercent`, and, when priced, `deviation.costPercent` are signed percentages (positive means actual was higher), and the run row displays them. This is the acceptance comparison against the recorded sweep, not a precision claim: CLI-added system context, provider tokenization, cache behavior, and response length are not knowable from prompt characters. Capped or failed partial runs do not publish a misleading full-sweep deviation.

The server-side acceptance test exercises a two-file plus aggregate sweep through the HTTP API, records each operation in `.quality/usage/`, deliberately crosses a token cap, verifies the reviewed/skipped report, raises the cap, and verifies that the completed run reports a non-zero estimate deviation. The assertion intentionally checks deviation rather than equality.

## Freshness, caps, and execution

The API runs uncapped file reviews with bounded concurrency (`ReviewJobs:MaxConcurrency`, default `2`). Capped runs execute serially so concurrent files cannot all cross the boundary. Container sweeps continue after individual file failures and write the selected project, module, or namespace review after all file attempts finish.

Before every file and aggregate operation, the runner compares the current subject manifest hash, effective review-inputs hash, and requested model with the existing sidecar. A unit is `skipped-fresh` only when all three match, and the agent is not called. The run snapshot and durable status include these skips; aggregate freshness is exposed through `aggregateState`. Set `force: true` on the start or estimate request to bypass this gate for every unit in the run.

A run may use one token cap or one cost cap. Omitting both inherits the repository's default. Enforcement happens in `ReviewJobService` at durable review-operation boundaries: once recorded usage reaches the cap, no next file or aggregate operation starts. The operation that crosses the threshold is allowed to finish cleanly, so actual spend can exceed the cap by at most that operation. Remaining files are persisted as `skipped`, the aggregate is reported as `skipped` when applicable, and the run ends as `capped` with a stop reason and complete reviewed, failed, and skipped counts.

A capped run is resumable without repeating completed files. `POST /api/review/runs/{id}/resume` accepts a higher `{ "tokenCap": ... }` or `{ "costCap": ... }`. Skipped units return to `queued`, while done and failed units remain durable. The server rejects a replacement cap already below current spend. Repository defaults are configured with `defaultReviewTokenCap` or `defaultReviewCostCap` (mutually exclusive) in the repository registration UI or API.

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

Every terminal revision is also written create-only to
`.quality/review-history/runs/<runId>/revision-<revision>-<contentHash>.json`. The
canonical report remains the latest export projection, while this per-run layout never
rewrites an earlier capped, failed, cancelled, or completed terminal observation. A
repeat write with identical content is idempotent; different content for an existing
run revision is reported as corruption. Startup can rebuild a missing canonical report
from the immutable archive or archive a canonical report produced before this layout
was introduced.

Model options come from the governed Token Economy snapshot described in
[`model-catalog-integration.md`](model-catalog-integration.md). The model and optional
thinking-level override are persisted in the manifest before enqueue and passed to the
same CodingAgentRunner request used for every file and aggregate operation.

At startup the API scans the registered repositories for durable runs. `queued` and formerly `running` runs are enqueued again; a file recorded as `done`, `failed`, or `skipped-fresh` is not reviewed again. A file that was `running` when the process stopped is returned to `queued`, because its sidecar write cannot be assumed to have completed. `paused` runs are restored but remain idle. Terminal `done`, `failed`, `cancelled`, and `capped` runs are loaded into recent history without being resumed.

The UI polls `GET /api/review/runs` every 1.5 seconds only while a run is queued or running. Each operation's recorded input/output usage is priced and persisted immediately, so the run row shows live tokens or cost spent against the cap. A terminal transition refreshes the hierarchy and the open file, so sidecar grades and staleness decorations update without a page reload. `POST /api/review/runs/{id}/pause` stops active work at the cancellation boundary while preserving completed files. Repository-scoped forms of all routes are also available. `DELETE /api/review/runs/{id}` permanently cancels queued, paused, or active work.

The Review panel presents active orchestration separately from committed history.
`GET /api/review/runs/history` reads the newest immutable revision, with canonical
reports as a compatibility fallback, so deleting disposable `.quality/runs/` state or
opening a fresh clone does not remove the terminal view. History rows
show terminal time, actual CLI/model/thinking level, result, finding count, and current human
assessment coverage. That coverage is a live projection from the independent assessment ledger;
it does not mutate the canonical run report.

`.quality/runs/` is ignored by Git because it is disposable orchestration working
data. Review sidecars remain the committed current-state truth, while canonical
run reports preserve the historical truth of each terminal execution.
