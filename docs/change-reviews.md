# Change-set reviews

## Shadow code-quality verdict (QS-117)

`POST /api/repos/{id}/change-review` (or `/api/change-review` for the default
repository) accepts `baseSha` and `headSha` (Git
commit names). Optional `cliType`, `model`, and `thinkingLevel` select the
isolated reviewer; the default is the product's code-review route. The endpoint
reads the exact Git diff without writing into the checkout or running a build. It sends
the changed hunks and the effective enabled code rules to the reviewer. A range
whose key already has a stored verdict is answered from the
[change-review store](#change-review-store) without a model call. The
response conforms to [`change-review-verdict.v1.schema.json`](../schemas/change-review-verdict.v1.schema.json).

```json
{ "baseSha": "<40-character commit>", "headSha": "<40-character commit>" }
```

The `verdict` is `pass`, `concerns`, or `block`. Each finding cites a catalogue
rule, a path and changed line (`side: head` for added lines or `side: base` for
removed lines), and `evidenceChecked` and `missing` text. The API
rejects a reviewer finding outside a changed line or an applicable enabled rule
as `unparseable`, with no partial pass. Reviewer failures have an explicit
infrastructure verdict; callers must never turn one into pass. Fingerprints
are derived from the rule, path, line, and changed source text, so replaying
the same range produces the same finding identity. The response also binds the
effective rule-set hash and policy-file hash and records model, thinking level,
usage, and duration. No existing gate consumes this endpoint.

The repository owns `.quality/policy.json`, schema version 1:

```json
{ "schemaVersion": 1, "blockingRules": ["*"], "blockingSeverities": ["critical", "high"] }
```

`blockingRules` names catalogue IDs or `*`; `blockingSeverities` names one or
more of `critical`, `high`, `medium`, `low`, and `info`. Both conditions must
match for a finding to block. Enabled code rules outside those thresholds raise
concerns. Applicable security-category code rules use the same policy thresholds.
Standing debt and a new endpoint alone do not block.
The default applies when the policy file is absent. The file is versioned in
this repository; a registered project can commit its own version.

This prepares the QS-118 Dossier's recommended option B in shadow mode.
Under the existing QS-W6 decision, Agent Studio remains the decision owner;
this endpoint does not change its gates or review outcome.

## Change-review store

A verdict for a commit range is computed at most once (AGT-3352 Dossier,
decisions D5 and D7 option a). Agent Studio's Review Plane computes the verdict
for a card's range `merge-base(integration ref, Result-SHA)..Result-SHA` and
pushes it here. Quality Studio stores its own results in the same place, so
opening a range never pays for a second model call.

### Key and record

A verdict is identified by its **key**: repository id, `baseSha`, `headSha`,
`policyHash`, `ruleSetHash` and `catalogueVersion`. The model, thinking level
and reviewer CLI are not part of the key. A stored verdict answers every later
request for its key, whichever model that request names.

Records live in the project's [data root](data-root.md), outside the checkout:

```
<data root>/projects/<project-key>/change-reviews/<baseSha>/<headSha>/<key digest>.json
```

```json
{
  "schemaVersion": 1,
  "key": { "repository": "proj-002", "baseSha": "…", "headSha": "…", "policyHash": "sha256:…",
           "ruleSetHash": "sha256:…", "catalogueVersion": "1.6.0", "digest": "sha256:…" },
  "keyDigest": "sha256:…",
  "provenance": { "producer": "agent-studio-review-plane", "cardKey": "AGT-3352", "round": 2,
                  "attemptId": "…", "clientId": "agent-studio" },
  "recordedAt": "2026-11-01T14:32:00+00:00",
  "documentDigest": "sha256:…",
  "document": { "schema": "…change-review-verdict.v1.schema.json", "schemaVersion": 1, "verdict": "concerns", … }
}
```

`document` is the `change-review-verdict.v1` document exactly as it was
produced. `producer` is `quality-studio` (computed by `POST change-review` on
this host) or `agent-studio-review-plane` (ingested). `cardKey`, `round` and
`attemptId` name the Agent Studio card round that was judged. `clientId` is the
API client that stored the record. Records are disposable like all data-root
content, but deleting one means the next request for its key pays for a model
call again.

A key holds one record:

| Stored record | New document for the same key | Result |
| --- | --- | --- |
| none | any | stored |
| same document (same compact JSON) | | unchanged, nothing written |
| `pass`, `concerns` or `block` | a different document | kept; ingest answers `409` |
| `provider-unavailable`, `timeout`, `auth-failure` or `unparseable` | any other document | replaced |

An infrastructure verdict is stored so the range can show it as
**unavailable**. It never answers a lookup, so the next request for the key
calls the reviewer again.

### Cache-first `POST change-review`

`POST /api/repos/{id}/change-review` first resolves the key: both commits, the
effective rule set and `.quality/policy.json` of the registered checkout. It
reads the store before it creates a reviewer:

- A decisive record for the key is returned unchanged. No model call is made.
  The response header `Quality-Studio-Change-Review: stored` says so.
- On a miss, or when only an infrastructure verdict is stored, the reviewer
  runs once. The result is stored with producer `quality-studio`, and the
  response carries `Quality-Studio-Change-Review: computed`.

Concurrent requests for one key wait for the first, so a burst of identical
requests costs one model call. The response body is the stored document, so a
repeated request returns a byte-identical answer. The route stays on the
`spend` rate limit, including when the answer comes from the store.

### Ingest from Agent Studio

`PUT /api/v1/repos/{id}/change-reviews` stores a document that Agent Studio's
Review Plane computed. It authenticates like every other mutation: a bearer
token, plus `X-Client-Id` in hosted mode. In local mode there are no tokens.

```json
{
  "document": { "schema": "https://agent-orchestrator.dev/quality/schemas/change-review-verdict.v1.schema.json",
                "schemaVersion": 1, "repository": "proj-002", "baseSha": "…", "headSha": "…", "verdict": "pass",
                "policyHash": "sha256:…", "ruleSetHash": "sha256:…", "catalogueVersion": "1.6.0", "findings": [] },
  "provenance": { "producer": "agent-studio-review-plane", "cardKey": "AGT-3352", "round": 2, "attemptId": "…" }
}
```

| Answer | When |
| --- | --- |
| `201` `{"outcome":"stored","record":…}` | The key had no record. |
| `200` `{"outcome":"unchanged",…}` | The same document is already stored. Repeating an ingest is safe. |
| `200` `{"outcome":"replaced",…}` | An infrastructure verdict for the key was replaced. |
| `409` | A different decisive verdict is already stored. The response names `keyDigest` and `storedDocumentDigest`. Retrying cannot succeed. |
| `422 Unsupported change-review document version` | `schema` or `schemaVersion` is not `change-review-verdict.v1` / `1`. |
| `422 Change-review document is invalid` | The document departs from the v1 schema, `repository` is not the route's id, or `provenance.producer` is unknown. |

The key is read from the document. Commits do not have to exist on this host,
so a verdict can be stored before the checkout has fetched them. Requests are
limited by `QualityStudio:Security:MaxRequestBodyBytes` (64 KiB by default).
Raise it if documents with many findings are refused with `413`.

### Reading a range

`GET /api/v1/repos/{id}/change-reviews?base=<sha>&head=<sha>[&previous=<sha>]`
returns every stored record for the range, the newest first: the record whose
rule set was recorded last comes first. It never calls a reviewer. All three
SHAs must be full 40-character lowercase commit names; anything else answers
`400`.

```json
{
  "repository": "proj-002", "baseSha": "…", "headSha": "…",
  "commits": { "base": true, "head": true, "previous": true },
  "currentKey": { … },
  "permissions": { "runReview": true },
  "records": [ { "key": …, "provenance": …, "recordedAt": "…", "current": true, "decisive": true, "document": … } ],
  "excerpts": { "<finding fingerprint>": [ { "kind": "added", "baseLine": null, "headLine": 88, "text": "…", "anchor": true } ] },
  "roundOnly": { "previousHeadSha": "…", "available": true, "fingerprints": [ "sha256:…" ] }
}
```

- `commits` says whether each SHA exists in this host's checkout. A missing
  commit does not hide stored verdicts. It does make `currentKey`, `excerpts`
  and `roundOnly` unavailable.
- `currentKey` is the key `POST change-review` would use now. `current` marks
  the record stored under it. Other records were judged under another rule set
  or policy and stay valid for their own key.
- `excerpts` holds the diff lines around each finding, from the same two-dot
  diff the reviewer saw. `anchor` marks the cited line.
- `roundOnly` appears when `previous` (the previous round's head) is given. It
  lists the findings on lines that `previous..head` changed: head-side findings
  on a line that round added, and base-side findings in a file from which that
  round removed lines. This is decision D5's "this round only" view inside the
  cumulative merge-base range.
- `permissions.runReview` is true for every caller this host authenticates
  today, that is Local mode and bearer clients.

### Change view

The deep link `/?repo=<id>&view=change&base=<sha>&head=<sha>[&card=<key>][&previous=<sha>]`
opens the range in the UI (see [api.md](api.md#deep-link-v1)). The view shows:

- the verdict: Pass, Concerns or Block. All four infrastructure verdicts show
  as **Unavailable** with their reason, never as Pass;
- severity counts, and each finding with its rule, location and evidence, above
  the diff lines it cites;
- provenance: producer, card, round, attempt, time, model, rule set and policy;
- a "This round only" filter when `previous` is given;
- **Not reviewed** when the range has no verdict. Opening the view never starts
  a review. Operators get **Run review**, which calls the spend route
  `POST change-review`. It is offered only when no decisive verdict is stored
  and both commits are present. After an infrastructure verdict it acts as a
  retry;
- **Commit not present on this host**, with the missing SHA, when the checkout
  lacks the base or head commit.

`GET /api/v1/capabilities` announces this store as feature
`change-review-store` under contract `agent-studio-integration/1`.

The [20-task shadow comparison](../results/shadow-comparison.md) records the
operator-provided SHA pairs, retained endpoint responses, exact agreement rate,
and each disagreement. Re-run it with
`node scripts/compare-shadow-verdicts.mjs results/shadow-pairs.json results/shadow-comparison.json`.

Standing review metadata answers how a unit scores until its reviewed inputs
change. A change review answers a different question: what one integration
transition changed in that standing evidence. It is project-owned at
`changes/<merge-commit>.json` below the project's data root, outside the reviewed
checkout ([`data-root.md`](data-root.md)), and never replaces a unit sidecar or an
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

- before/after grades for touched reviewed units, directly from the review
  sidecars each of the two commits carries;
- new, resolved, and persisting findings by stable fingerprint (with `id` only
  as a legacy fallback);
- units newly made stale, including the changed or missing reviewed input;
- new, changed, and removed HTTP boundaries in touched files;
- coverage delta through `IChangeCoverageProvider`, or an explicit
  `unavailable` value until coverage ingestion exists;
- added/deleted lines, path operations, touched-file count, and repository
  blast radius.

The first three read sidecars out of the Git trees of the two commits, so they
see only sidecars that were committed there. Sidecars are now generated into the
project's data root instead of the checkout, so a transition after that move
carries no sidecar evidence on either side; boundaries, coverage, and churn are
computed from source and diff and are unaffected.

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

`--no-write` suppresses `changes/` persistence in the data root. The explicitly
named portable output file is still written atomically, and the command never
stages or modifies repository content. That output file is the only thing the
command can put inside the worktree, so write it elsewhere when a completely
clean `git status` is required.

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

The 20-transition sample measured for this repository recorded 922,304
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
