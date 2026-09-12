# QS-59 status

Status: resolved (superseded, not abandoned)
Phase: no redelivery performed — see rationale below
Primary reference: `docs/operations/performance/index.html` (owned by card `QS-82`)

## What the operator's rework note asked for

The 2026-09-12 20:50 rework note asked to rebase the prior redelivery (commit
`f0ac5721`, a standalone `QS-59` dossier last valid against `origin/main da620fa`)
onto current `origin/main`, resolve an add/add conflict on
`scripts/perf/tree-payload-perf.mjs` against QS-82's own copy of that path, and
redeliver with no feature rework.

## Why a literal rebase-and-redeliver is not the right move now

`git status` on this branch shows a clean tree, and `git rev-parse HEAD
origin/main` are identical (`5b90eed3`) — there is no divergence to rebase and
no conflict left to resolve, because `origin/main` has moved past the point
the rework note describes:

- `scripts/perf/tree-payload-perf.mjs` exists exactly once on `origin/main`
  (QS-82's version, last touched by commit `9dd114dd`). There is nothing to
  merge; the add/add conflict the note describes was between the salvage
  branch and an earlier `origin/main` that has since been superseded.
- `docs/operations/performance/` on `origin/main` is no longer the 522-line,
  `status: decision-pending` / `phase: decision-ready` document `f0ac5721`
  shipped. It is now a 228-line "living dossier" owned by card `QS-82`
  (`workbench.json`: `status: "documented"`, `phase: "verified"`,
  `sourceTaskKeys: ["QS-59","QS-54","QS-78","QS-82"]`, `updatedAt:
  2026-09-07T21:40:00Z`). Its section 2, "QS-59 cost blocks and selection",
  restates this card's three cost blocks with their evidence numbers and
  current disposition, and its sections 3–11 document that the top block this
  card flagged (`/api/tree` sending 6.1x more data than needed) was approved,
  implemented, and re-verified across five separate re-gates through
  2026-09-07.
- `PERF.md` (repo root) independently confirms the same thing: its "QS-82 lazy
  tree transport" section measures the fix "on the same 3,927-file Agent
  Studio repository used by the QS-59 dossier" and quotes this card's own
  restart-to-usable baseline (`QS-59: 10,004.99 ms median`) as the pre-fix
  number being improved on.

Redelivering `f0ac5721`'s standalone, `decision-pending` document at
`docs/operations/performance/index.html` would silently overwrite this
already-shipped, gate-verified content with a stale draft that has since been
overtaken by events (the decision it was asking the operator to make was
already made and implemented under QS-82). That is a correctness regression,
not a conservative conflict resolution, so it was not done.

## What this delivery does instead

- Leaves `docs/operations/performance/` exactly as it is on `origin/main`
  (untouched).
- Adds this file and `results/deliverables.md` as the closure record for
  QS-59, so the 2026-08-11 mandate's documentation requirement is met without
  disturbing the merged QS-82 artifact.
- Recommends the operator close QS-59 with a pointer to `docs/operations/performance/index.html`
  (card QS-82) as the disposition of its analysis and recommendation, rather
  than expecting a second, separate dossier at a colliding path.

## Verification this run

- `git status`: clean.
- `git rev-parse HEAD origin/main`: both `5b90eed3d908883f834f52e4533b8b47cf256ba0` — branch has zero divergence from `origin/main`.
- `find` for `scripts/perf/tree-payload-perf.mjs`: exactly one file, no duplicate/conflicted copy.
- `grep` confirmed `docs/operations/performance/index.html` references only `frontend/tests/*.mjs` and `scripts/verify-tree-v2-contract.mjs` / `scripts/measure-tree-transport.mjs` for its evidence, not `scripts/perf/tree-payload-perf.mjs` — no dangling reference to fix.
