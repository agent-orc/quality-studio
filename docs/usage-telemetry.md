# Review usage telemetry

Every agent-backed review operation writes the runner-reported model, CLI type,
token counts, duration, timestamp, review kind, hierarchy level, path, and run
identifiers to two places:

- the review-meta `reviewer.usage` block, alongside `reviewer.model` and
  `reviewer.runId`; and
- the project's append-only ledger at `usage/YYYY-MM.jsonl` below its data root,
  outside the reviewed checkout ([`data-root.md`](data-root.md)).

Token fields are `null` when a CLI does not report them; zero means the CLI
explicitly reported no tokens in that category. Ledger entries use the versioned
contracts in `schemas/usage-ledger.v1.schema.json`,
`schemas/usage-ledger.v2.schema.json`, and `schemas/usage-ledger.v3.schema.json`.
In every version, `runId` is the ID returned by the CLI for one operation.
Version 2 adds `reviewRunId`, the durable sweep/job ID shared by all file and
aggregate operations in the review. Version 3 (written since 2026-09-06) adds
`modelSource` and makes `reviewRunId` optional for standalone CLI reviews.
Existing v1 and v2 lines remain valid and are never migrated or rewritten.

## Model attribution

The CLIs report the model they were asked to run, not a model they chose
themselves. A run that named no model therefore used to be recorded as the
literal `runner-default`, which no price catalog can resolve. Since version 3 a
review without an explicit model runs the synchronized routing policy's route
for its CLI, and that model id is passed to the CLI explicitly, so `model` is a
real id in the sidecar (`reviewer.model`, `reviewer.requestedModel`), in the run
manifest, in the run response, and in the ledger. `modelSource` says how the
model was chosen:

- `explicit` — the caller named the model.
- `policy-default` — no model was named; Quality Studio resolved the routing
  policy's route for the CLI (Codex: the recommendation; Claude: the policy's
  provider fallback for the recommended route, or the strongest fallback-eligible
  Claude model when the policy withholds every fallback from that route).
- `runner-default` — no model was named and the policy routes nothing for the
  CLI (Gemini, Antigravity, custom adapters); the CLI's own default served the
  run and `model` stays the literal `runner-default`.

Ledger lines written before this version keep `runner-default` where the model
was never named; they are historical evidence and are not rewritten.

## Cost transparency and budgets

Every review operation is priced when it is recorded: the ledger entry carries
`cost` with `total`, `currency`, and `status` at the catalog price valid at the
operation timestamp, and the API host logs the same figures per operation
(event `ReviewOperationUsage`) next to the run id. `total` is `null`, never a
silent zero, when the model is unknown to the price catalog (`unknownModel`) or
the catalog has no price for that date (`noPriceForDate`). Entries written
before costs were recorded are priced at query time, so `GET /api/usage`
returns `estimatedCost`, `costCurrency`, and `unpricedRuns` over the whole
history. The synchronized Token Economy price snapshot is the only price source;
when its validity window ends, operations become unpriced until the catalog is
synchronized again (`npm run catalog:sync`).

Budgets are cost budgets, not time budgets. A review run can carry a token cap
and a cost cap (`tokenCap`, `costCap` on the start request; the repository's
`DefaultReviewTokenCap` applies when none is given), and the run stops with a
recorded stop reason when either is reached. A cost cap requires a priced model;
the API refuses a cost cap for an unpriced route instead of pretending to
enforce it. Subscription-backed CLIs whose provider meters usage in windows
rather than per token run without a cap by design; their operations are still
estimated and logged so the ledger stays complete.

`GET /api/usage?since=&kind=` (and its repository-scoped equivalent) reads the
ledger and returns totals, model/kind/day/review-run aggregates, and at most 50
recent entries. `byReviewRun` groups v2 entries by `reviewRunId`, making a
completed sweep's token total recoverable without the in-memory job object and
after an API restart. A v1 entry has no sweep ID, so it is retained as a
singleton group keyed by its CLI `runId`. A malformed historical JSONL line is
skipped so one interrupted write cannot make the rest of the ledger unavailable.

The Usage button in the top bar opens the repository history view. It shows
input-plus-output token spend, model and daily aggregates, and keyboard-accessible
recent-entry details containing both run identifiers.

## Ledger durability

`usage/YYYY-MM.jsonl` is the spend history of one analysed project. The files are
monthly and append-only; do not compact, reorder, rewrite, or discard prior
lines.

The ledger used to be committed repository history, kept mergeable with Git's
union merge driver and staged through the repository's normal development
workflow. It is not any more. A file that grows on every review kept the analysed
checkout permanently dirty, and that is what stopped the studio from being run
against its own repository; a reproducible record of what a run spent is not
history the reviewed repository owes anyone. The ledger is generated data like
every other run artefact, so it is only as durable as the data root that holds
it — back that up when the recorded spend matters, because nothing else preserves
it.

This policy begins with the ledger data available for each project. Missing
historical entries are not fabricated retroactively.

## Quota ownership

Quality Studio uses `CodingAgentRunner.Quota.QuotaService` as quota truth. The
runner already owns provider-specific authentication and parsing for Claude and
Codex, exposes a shared per-user cache, and can harvest rate-limit events from
runs without another provider request. Introducing a second Token Economy
adapter here would duplicate that ownership. `GET /api/quotas` exposes a
presentation-safe projection; the topbar refreshes it every 60 seconds. Missing
credentials, missing session logs, probe failures, and an empty cold cache are
shown as “Quota unavailable” and never block reviews.
