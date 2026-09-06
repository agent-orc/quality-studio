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

Finding lifecycle state is project-owned in the external data root at `.quality/findings/state.json`. Records are keyed by fingerprint and contain `open`, `accepted`, `waived`, `false-positive`, or `resolved`, plus author, reason, timestamp, and optional expiry. New findings start open. A finding absent from the replacement review is retained as resolved. A resolved finding that reappears becomes open. Expired accepted, waived, or false-positive state also becomes open.

Review metadata remains an observation; state is projected onto it when it is read. Waived and false-positive findings remain visible and counted, but their severity-weighted share of the agent's score deficit is removed from the effective grade. Resolved findings likewise do not affect the grade. If all reported findings are excluded, the effective grade is 100. Accepted findings remain part of the grade. Severity weights are critical 16, high 8, medium 4, low 2, and info 1.
