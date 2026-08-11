# Finding identity and lifecycle

Review agents must return a stable `ruleId` naming the guideline or rule that produced each finding. Agent-provided ids are labels only; the runner validates every location against the reviewed subject and assigns both the persisted `id` and `fingerprint`.

The fingerprint canonicalization is `quality-studio-finding-v1` followed by NUL, the repository-relative path using `/`, NUL, the primary location's code snippet after line endings are changed to LF, leading/trailing whitespace is removed, and every remaining whitespace run is replaced by one ASCII space, NUL, and the trimmed case-sensitive `ruleId`. The UTF-8 bytes are SHA-256 hashed and formatted as `sha256:<lowercase hex>`. The finding id is `finding-<the same lowercase hex>`.

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

Finding lifecycle state is repository-owned in `.quality/findings/state.json`. Records are keyed by fingerprint and contain `open`, `accepted`, `waived`, `false-positive`, or `resolved`, plus author, reason, timestamp, and optional expiry. New findings start open. A finding absent from the replacement review is retained as resolved. A resolved finding that reappears becomes open. Expired accepted, waived, or false-positive state also becomes open.

Review metadata remains an observation; state is projected onto it when it is read. Waived and false-positive findings remain visible and counted, but their severity-weighted share of the agent's score deficit is removed from the effective grade. Resolved findings likewise do not affect the grade. If all reported findings are excluded, the effective grade is 100. Accepted findings remain part of the grade. Severity weights are critical 16, high 8, medium 4, low 2, and info 1.

## Independent assessment, resolution, and suppression

The current decision model keeps three questions independent:

- assessment records whether a human considers the observation `unassessed`, `confirmed`, `dismissed`, or `disputed`;
- resolution records remediation as `open`, `planned`, `fixed`, `risk-accepted`, or `obsolete`; and
- suppression is repository policy that hides a matching observation from the effective score without deleting it.

Assessment and resolution events are append-only JSONL under
`.quality/findings/assessments/YYYY-MM.jsonl` and
`.quality/findings/resolutions/YYYY-MM.jsonl`. Each mutation carries the timestamp the caller
loaded, including `null` when no prior event exists, so concurrent changes return HTTP 409
instead of silently replacing one another. Their contracts are
`schemas/finding-assessment.v1.schema.json` and
`schemas/finding-resolution.v1.schema.json`.

Suppressions live in the revisioned `.quality/findings/suppressions.json` contract. Exact
fingerprint matching is the safe default. Broader rules may combine rule id, repository path
glob, review kind, and source kind; the API requires a preview plus explicit confirmation.
Rules may expire and may be disabled. Matching by mutable title or regular expression is not
supported. The original finding remains visible as suppressed, and only its severity-weighted
score deficit is excluded.

The former lifecycle file remains readable. Its values are projected without rewriting it:
`accepted` becomes confirmed/open, `waived` confirmed/risk-accepted, `false-positive`
dismissed/obsolete, and `resolved` unassessed/fixed. New assessment and resolution events take
precedence independently.
