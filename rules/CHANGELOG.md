# Rule library change history

Library-level version history. Per-rule history lives in each rule file's own
`## Change history` section.

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
