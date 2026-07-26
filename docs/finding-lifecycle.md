# Finding identity and lifecycle

Review agents must return a stable `ruleId` naming the guideline or rule that produced each finding. Agent-provided ids are labels only; the runner validates every location against the reviewed subject and assigns both the persisted `id` and `fingerprint`.

The fingerprint canonicalization is `quality-studio-finding-v1` followed by NUL, the repository-relative path using `/`, NUL, the primary location's code snippet after line endings are changed to LF, leading/trailing whitespace is removed, and every remaining whitespace run is replaced by one ASCII space, NUL, and the trimmed case-sensitive `ruleId`. The UTF-8 bytes are SHA-256 hashed and formatted as `sha256:<lowercase hex>`. The finding id is `finding-<the same lowercase hex>`.

Finding lifecycle state is repository-owned in `.quality/findings/state.json`. Records are keyed by fingerprint and contain `open`, `accepted`, `waived`, `false-positive`, or `resolved`, plus author, reason, timestamp, and optional expiry. New findings start open. A finding absent from the replacement review is retained as resolved. A resolved finding that reappears becomes open. Expired accepted, waived, or false-positive state also becomes open.

Review metadata remains an observation; state is projected onto it when it is read. Waived and false-positive findings remain visible and counted, but their severity-weighted share of the agent's score deficit is removed from the effective grade. Resolved findings likewise do not affect the grade. If all reported findings are excluded, the effective grade is 100. Accepted findings remain part of the grade. Severity weights are critical 16, high 8, medium 4, low 2, and info 1.
