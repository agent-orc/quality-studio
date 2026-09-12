# Rule library change history

Library-level version history. Per-rule history lives in each rule file's own
`## Change history` section.

## 1.4.0 (2026-09-12)

- Added evidence-first finding guidance (QS-GN-004) for all review kinds.
- Published shared review methodology and exact metric explanations for the tool and website.

## 1.3.0 (2026-09-12)

- Added repository-owned architecture contracts and deterministic structure findings (QS-GN-003).
- Angular component structure now reflects declared layers and focused child component composition (QS-NG-003).
- Typography minimum violations map to the parser-based ESLint/SARIF check (QS-NG-001).

## 1.2.0 (2026-09-06)

The library covers three review kinds and three technologies. Eighteen rules added; no existing
rule changed.

- .NET security (`QS-CS-005`..`QS-CS-008`): repository path confinement, external process
  arguments, secrets in logs and responses, bounded deserialization.
- .NET performance (`QS-CS-009`..`QS-CS-012`): blocking I/O on request paths, cache keys and
  bounds, one command per scope instead of per project, timeouts and queue supervision.
- Angular security (`QS-NG-006`..`QS-NG-009`): `postMessage` origin and payload, sanitizer
  bypasses, credentials in client state, URL construction and navigation targets.
- Angular performance (`QS-NG-010`..`QS-NG-013`): allocating template expressions, bounded render
  windows, the production bundle budget, main-thread work.
- Language-independent (`QS-GN-001`, `QS-GN-002`): untrusted content as data, explicit resource
  bounds. Both apply to every adapter.

Every new rule is grounded in this repository: its good example is code that already exists here,
and its rationale names the finding or measurement it comes from.

## 1.1.0 (2026-09-06)

The rule format gained the two fields the generated catalogue needs to route a rule.

- `kinds` (required): which review kinds a rule is injected into (`code`, `security`,
  `performance`). All nine seed rules declare `[code]`.
- `## Detection` (required): what a reviewer looks at, and what does not count as a violation.
- `technology` accepts `generic` for language-independent rules.
- Every seed rule moved to 1.1.0; no statement or severity changed.

## 1.0.0 (2026-08-27)

Initial seed library — QS-90, closing the "empty rule library" gap named in
`docs/operations/quality-concept/index.html`.

- Angular seed set (5 rules): `QS-NG-001` design tokens, `QS-NG-002` standard-component reuse,
  `QS-NG-003` component structure, `QS-NG-004` template hygiene, `QS-NG-005` OnPush change
  detection.
- C#/.NET seed set (4 rules): `QS-CS-001` API shape, `QS-CS-002` DI patterns, `QS-CS-003` async
  hygiene, `QS-CS-004` test structure.
- All 9 rules ship `defaultOn: true` (default-on core).
