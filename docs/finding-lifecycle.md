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

## Finding ignore list

Finding-level ignore policy is separate from lifecycle state and from repository scope. The API owns atomic mutations of `.quality/findings/suppressions.json`; the browser never edits the file directly. Each rule matches the stable finding fingerprint, records its author, reason, creation time, and optional expiry, and uses a monotonically increasing document revision for optimistic concurrency. Because the match is independent of a review run id, it survives replacement and resumed runs.

Ignoring changes the default findings presentation only. It never deletes or rewrites the review observation and does not turn an ignored finding into a dismissal. The Review queue reports the ignored count and exposes the retained observations through its **Ignore list** control. Expired or removed rules immediately return their findings to the default queue. Path-level exclusions remain a distinct future-review scope feature in `.quality/scope.json`.

The API exposes the policy for both the default and repository-qualified routes:

- `GET /api/findings/suppressions`
- `POST /api/findings/suppressions`
- `DELETE /api/findings/suppressions/{id}?expectedRevision=<revision>`
