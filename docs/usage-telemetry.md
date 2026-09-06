# Review usage telemetry

Every agent-backed review operation writes the runner-reported model, CLI type,
token counts, duration, timestamp, review kind, hierarchy level, path, and run
identifiers to two places:

- the review-meta `reviewer.usage` block, alongside `reviewer.model` and
  `reviewer.runId`; and
- the project data-root append-only ledger at `.quality/usage/YYYY-MM.jsonl`.

Token fields are `null` when a CLI does not report them; zero means the CLI
explicitly reported no tokens in that category. Ledger entries use the versioned
contracts in `schemas/usage-ledger.v1.schema.json` and
`schemas/usage-ledger.v2.schema.json`. In both versions, `runId` is the ID returned
by the CLI for one operation. Version 2 adds `reviewRunId`, the durable sweep/job
ID shared by all file and aggregate operations in the review. Existing v1 lines
remain valid and are never migrated or rewritten.

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

## Storage and export policy

`.quality/usage/YYYY-MM.jsonl` is monthly append-only runtime data beneath the
project's external data root. It is not repository history and must not be copied
back into a checkout. Use a report export when usage evidence must be published.

This policy begins with the ledger data available in each repository. Missing
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
