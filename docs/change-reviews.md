# Change-set reviews

Standing review metadata answers how a unit scores until its reviewed inputs
change. A change review answers a different question: what one integration
transition changed in that standing evidence. It is repository-owned at
`.quality/changes/<merge-commit>.json` and never replaces a unit sidecar or an
Agent Studio task review.

## Subject and provider contract

`IChangeSetProvider` returns a `ChangeSet`: provider id, base commit, topic head,
optional merge commit, title, timestamp, and touched paths. The deterministic
review service consumes only that contract. `GitMergeRangeChangeSetProvider` is
the first provider. The tests also exercise a trivial in-memory provider; a
future pull-request or attributed Agent Studio commit provider does not require
changes to delta analysis or persistence.

For a two-parent Git merge, the first parent is `baseCommit`, the second parent
is `headCommit`, and the merge itself is `mergeCommit`. The reviewed tree and
diff end at the merge commit so conflict resolutions are included. For a
one-parent integration transition, `baseCommit` is its parent, `headCommit` is
the transition commit, and `mergeCommit` is omitted rather than invented. Its
head is used as the storage key. This distinction is why backfill works for
repositories that mix merge commits with squash or fast-forward integration.

## Deterministic evidence

Before any agent is called, the service computes:

- before/after grades for touched reviewed units, directly from committed
  review sidecars;
- new, resolved, and persisting findings by stable fingerprint (with `id` only
  as a legacy fallback);
- units newly made stale, including the changed or missing reviewed input;
- new, changed, and removed HTTP boundaries in touched files;
- coverage delta through `IChangeCoverageProvider`, or an explicit
  `unavailable` value until coverage ingestion exists;
- added/deleted lines, path operations, touched-file count, and repository
  blast radius.

Pure `R100` moves translate old paths to new paths while comparing evidence.
They produce the explicit statement “No quality delta: the change set only
moved files without changing their content.” A rename is still retained as
churn; it is not turned into a fictional quality event.

The boundary inventory currently recognizes ASP.NET minimal API and HTTP
attributes, Python route decorators, and Express-style registrations. Identity
includes repository path, HTTP method, and route. A same-line replacement is
reported as a changed boundary, while a new externally reachable method/route
is a regression and names its path, line, and touched unit.

## Agent judgement

`IChangeDeltaReviewer` receives the deterministic delta and the unified diff of
reviewable touched paths—never whole files or a repository sweep. The supplied
agent adapter requires exactly four named aspects:

- risk of the change;
- test evidence;
- scope discipline;
- architecture drift.

Agent judgement is optional at the command boundary. When it is not requested,
the artifact says `not-run`; deterministic facts and gate behavior are still
complete. This keeps backfill reproducible and lets pipelines use the gate
without requiring an agent.

## CLI and exit codes

Review one range:

```text
quality diff . --base <base> --head <head>
```

Backfill integration history:

```text
quality diff . --last 20 --branch <integration-ref>
```

Add `--agent` for the four-aspect judgement, `--no-write` for an ephemeral
check, and `--fail-on-regression` for gating.

Export a transport-neutral artifact for a fenced task-change subject:

```text
quality diff . --base <base> --head <head> --no-write \
  --format json --output <artifact.json> \
  --repository <stable-repository-id> \
  --review-policy-hash <sha256:caller-policy-hash>
```

`--format json` and `--output` are paired. The export uses
`change-review-evidence.v1.schema.json` and retains the complete v1 change
review while adding the discriminated task-change subject, provider-policy and
agent-prompt hashes, usage and duration when an agent ran, and common immutable
finding envelopes grouped as new, resolved, and persisting observations. The
subject keeps base, topic head, and reviewed result SHA distinct, including for
two-parent merges.

Without `--agent`, agent evidence is explicitly `unavailable` with the reason
that judgement was not requested; it is never represented as a pass. If
`--review-policy-hash` is omitted, the subject binds to the built-in QS provider
policy. If `--repository` is omitted, the Git worktree directory name is used,
so orchestrated callers should pass their stable registry identity.

`--no-write` suppresses `.quality/changes/` persistence. The explicitly named
portable output file is still written atomically, and the command never stages
or modifies repository content. Write the output outside the worktree when a
completely clean `git status` is required.

| Exit | Meaning |
|---:|---|
| 0 | Review completed; no regression when gating is enabled. |
| 1 | `--fail-on-regression` found at least one deterministic regression. |
| 2 | Invalid invocation, unavailable Git data, or review failure. |

A lower touched-unit grade, a new finding, a newly stale unit, a new or changed
boundary, or lower ingested coverage makes the deterministic verdict a
regression. The command prints the specific unit and boundary before returning.
It does not alter any Agent Studio merge path.

## Economy measurement

Each artifact records the actual character count of the diff sent for
judgement and the character count of the full post-change contents of those
same reviewable files. It also records file and diff-line counts. Small files
can have more diff framing than source text, so savings are honestly clamped at
zero rather than presented as negative efficiency.

The committed 20-transition sample under `.quality/changes/` measured 922,304
diff characters against 3,351,261 full-sweep characters: 72.48% less evidence
in aggregate. Seventeen of twenty transitions saved work, and median per-change
savings were 67.89%.

## Sample trajectory

These are the 20 first-parent integration transitions ending at
`96ab211e6275`. The JSON artifacts contain the named units, findings,
boundaries, stale reasons, churn, and economy measurements behind the compact
view.

| Transition | Verdict | Main deterministic fact | Saved |
|---|---|---|---:|
| `fe07e6e2bafa` | no quality delta | no standing evidence changed | 70.90% |
| `e3619ed227b0` | regression | new `POST /api/repos/import-from-agent-studio` | 70.65% |
| `e4cc1d147526` | regression | three new usage/quota endpoints | 71.27% |
| `49d892023caa` | regression | seven new stable findings | 0% |
| `ea9409b2deeb` | no quality delta | no standing evidence changed | 96.22% |
| `271d69aa03d3` | regression | API program review became stale | 84.36% |
| `b3169fe5d049` | regression | two new thread endpoints | 66.59% |
| `d38a8dc9c43c` | no quality delta | no standing evidence changed | 0% |
| `0d03986269a9` | no quality delta | no standing evidence changed | 69.19% |
| `85d781faf91b` | no quality delta | no standing evidence changed | 0% |
| `ae5889ec708e` | regression | four new pause/resume endpoints | 51.22% |
| `5d60b3c7c6e8` | regression | five existing API boundaries changed | 64.06% |
| `27875996b9aa` | no quality delta | no standing evidence changed | 29.88% |
| `12b834ad678e` | no quality delta | no standing evidence changed | 74.49% |
| `4716fcc4516f` | no quality delta | no standing evidence changed | 90.02% |
| `73f008505308` | regression | two new finding-state endpoints | 69.50% |
| `ad251d7575f5` | regression | stale unit and twelve new guideline endpoints | 65.54% |
| `7c8ae67b3227` | regression | new finding, stale unit, four sensor endpoints | 65.33% |
| `cb98322797dc` | regression | two new review-estimate endpoints | 64.64% |
| `96ab211e6275` | no quality delta | no standing evidence changed | 82.75% |
