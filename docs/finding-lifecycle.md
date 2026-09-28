# Finding identity and lifecycle

Review agents must return a stable `ruleId` naming the guideline or rule that produced each finding. Agent-provided ids are labels only; the runner validates every location against the reviewed subject and assigns both the persisted `id` and `fingerprint`.

The fingerprint canonicalization is `quality-studio-finding-v1` followed by NUL, the repository-relative path using `/`, NUL, the primary location's code snippet after line endings are changed to LF, leading/trailing whitespace is removed, and every remaining whitespace run is replaced by one ASCII space, NUL, and the trimmed case-sensitive `ruleId`. The UTF-8 bytes are SHA-256 hashed and formatted as `sha256:<lowercase hex>`. The finding id is `finding-<the same lowercase hex>`.

## Rule ids and fingerprint stability

Because the fingerprint covers `ruleId`, the id an agent cites is part of a finding's identity,
and a review may only cite ids that actually resolved for the unit under review — a rule from the
built-in library, a guideline from `.quality/inputs/`, or `built-in:<kind>`. `ReviewResponseParser`
canonicalizes a cited id against that set, ignoring case, and replaces anything else with
`built-in:<kind>` while logging `RuleIdRejected`. Without that check, an invented id would give
the same defect a different identity on every run, and its lifecycle state would be lost each time.

Attributing an existing finding to a named rule does produce a new observation: the old
fingerprint disappears and is retained as resolved, and the newly attributed one starts open. That
is a one-time effect per finding, and it is the correct reading — a review that can name the rule
it violated is a different, better-attributed statement than one that could not. The alternative,
removing `ruleId` from the fingerprint basis, would require a new canonicalization and would
invalidate every stored fingerprint at once, so the basis and the
`quality-studio-finding-v1` canonicalization are unchanged.

## Identity across reruns

The fingerprint is computed from the code a finding's range encloses, and an agent that reports the
same defect again rarely chooses the identical range. On 2026-09-28 a forced rerun of the Agent Studio
checkout marked 23 findings that still existed as resolved because one line more or less gave them a
new fingerprint. `FindingIdentity.Assign` therefore compares each new finding with the earlier ones
before it assigns an identity:

1. A new finding whose computed fingerprint equals an earlier one keeps that identity.
2. Otherwise it adopts the identity of an earlier finding with the **same rule id**, the **same path**,
   the **same anchor content hash** (`capturedExcerpt.contentHash`: SHA-256 over the file with LF line
   endings — the code both anchors point into is identical), and **overlapping anchor lines**. The
   largest overlap wins, then the nearest start line.

Each earlier identity is given to at most one new finding, and exact fingerprints are settled first,
so an overlapping neighbour cannot take an identity another finding reproduces exactly. The agent's
title, description and recommendation take no part. A finding that keeps an earlier identity keeps its
id, fingerprint, lifecycle state, suppression and threads; its anchor is updated to the new span, which
is what the next rerun compares against. When the file content changed, only the exact fingerprint can
carry an identity, as before. The earlier findings are the previous sidecar's plus the not re-observed
ones below.

## Not re-observed is not resolved

A previous finding the latest review did not report again is resolved only when the code it is
anchored in changed. When the file still has the content hash the finding was observed on, a missed
finding is only **not re-observed** (`not-reobserved`): a model that misses a finding once has not
shown that it was fixed. An `open` finding becomes `not-reobserved`; `accepted`, `waived` and
`false-positive` stay as a person set them. Findings without a runner-measured anchor — deterministic
sensor results, for instance — have nothing to compare and are resolved as before.

The sidecar no longer lists a not re-observed finding, so its lifecycle record keeps the anchor it was
last seen at (`lastObservedContentHash`, `lastObservedRange`). A later review matches against it like
against a sidecar finding: re-reported on the same code, it becomes `open` again with the reason
"Finding was observed again by review"; missing after the code changed, it becomes `resolved`. The
anchor is dropped when a review reopens or resolves the finding; a human disposition set meanwhile
keeps it. Only a review that was given the finding's rule considers it, so a security review of the
same file neither re-observes nor resolves a not re-observed code finding. `not-reobserved` is set only
by review merge and cannot be chosen in the UI or API. A finding that a sidecar lists is observed by
that sidecar, so the grade projection and run reports read it as `open` there. The canonical run report's delta lists
missed findings on unchanged code under `notReobserved`, not under `resolved`.

## Immutable interchange envelope

`schemas/quality-finding.v1.schema.json` defines the finding observation shared
with external review callers. It keeps the review-meta v2 vocabulary—rule,
severity, title, description, recommendation, locations, fingerprint, and
producer—but deliberately excludes grades and mutable lifecycle disposition.
Its subject is discriminated as either `standing-unit` or `task-change`. A task
change binds repository identity, base SHA, topic head SHA, reviewed result SHA,
and caller review-policy hash. Task-level delivery deficiencies may use an empty
location list.

Location-free task findings declare
`quality-studio-task-finding-text-v1`: the canonicalization label, trimmed rule
id, title, description, and recommendation are separated with NUL characters;
line endings become LF and each whitespace run becomes one ASCII space before
the UTF-8 bytes are SHA-256 hashed. Lifecycle state remains product-owned and
references the immutable fingerprint rather than travelling in the envelope.

Finding lifecycle state is project-owned in `findings/state.json` below the project's data root, outside the analysed checkout ([`data-root.md`](data-root.md)). Records are keyed by fingerprint and contain `open`, `accepted`, `waived`, `false-positive`, `resolved`, or `not-reobserved`, plus author, reason, timestamp, and optional expiry. New findings start open. A finding absent from the replacement review is retained as resolved when its code changed, and as not re-observed when it did not (see above). A resolved or not re-observed finding that reappears becomes open. Expired accepted, waived, or false-positive state also becomes open.

Review metadata remains an observation; state is projected onto it when it is read. Waived and false-positive findings remain visible and counted, but their severity-weighted share of the agent's score deficit is removed from the effective grade. Resolved findings likewise do not affect the grade. If all reported findings are excluded, the effective grade is 100. Accepted findings remain part of the grade. Severity weights are critical 16, high 8, medium 4, low 2, and info 1.

## Finding Ignore list

The repository-owned `.quality/findings/suppressions.json` document is the persistent finding Ignore list; `schemas/finding-suppressions.v1.schema.json` defines its exact contract. Its revisioned rules match exact stable fingerprints and record a required author, reason, creation time, and optional expiry. API-owned create and remove operations use optimistic revision checks and atomic replacement; the browser never writes this file directly.

An active rule hides its finding from the default queue and exact source highlighting and removes its severity-weighted deficit from the effective grade. The observation remains in review metadata, remains queryable through the Suppressed filter, and keeps its independent lifecycle state. Expired rules remain visible in the Ignore list for audit but no longer suppress matching findings.
